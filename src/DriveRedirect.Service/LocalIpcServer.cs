using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Configuration;
using DriveRedirect.Infrastructure.Data;
using DriveRedirect.Infrastructure.Installers;
using DriveRedirect.Infrastructure.Installers.Adapters;
using DriveRedirect.Infrastructure.Security;
using DriveRedirect.Infrastructure.Shell;
using DriveRedirect.Infrastructure.Windows;
using Serilog;

namespace DriveRedirect.Service;

public sealed class LocalIpcServer : IAsyncDisposable
{
    private const int MaxRequestBytes = 2 * 1024 * 1024;
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private readonly AppRepository _repository;
    private readonly RedirectionPlanner _planner;
    private readonly AdapterRegistry _adapters;
    private readonly ShellIntegrationManager _shell;
    private readonly IpcTokenStore _tokenStore;
    private readonly AppPaths _paths;
    private readonly WindowsDiskProbe _diskProbe;
    private readonly InstallerProcessMonitor _monitor;
    private readonly ServiceRuntimeState _state;
    private readonly ConcurrentDictionary<string, BrokerRate> _brokerRates = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _adminLoop;
    private Task? _brokerLoop;

    public LocalIpcServer(
        AppRepository repository,
        RedirectionPlanner planner,
        AdapterRegistry adapters,
        ShellIntegrationManager shell,
        IpcTokenStore tokenStore,
        AppPaths paths,
        WindowsDiskProbe diskProbe,
        InstallerProcessMonitor monitor,
        ServiceRuntimeState state)
    {
        _repository = repository;
        _planner = planner;
        _adapters = adapters;
        _shell = shell;
        _tokenStore = tokenStore;
        _paths = paths;
        _diskProbe = diskProbe;
        _monitor = monitor;
        _state = state;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _adminLoop = Task.Run(() => AcceptLoopAsync(true, _cts.Token));
        _brokerLoop = Task.Run(() => AcceptLoopAsync(false, _cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_cts is null)
            return;
        _cts.Cancel();
        foreach (var loop in new[] { _adminLoop, _brokerLoop })
        {
            if (loop is null)
                continue;
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch
            {
            }
        }
        _cts.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(bool admin, CancellationToken ct)
    {
        var pipeName = admin ? IpcNames.AdminPipe : IpcNames.BrokerPipe;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                var security = CreatePipeSecurity(admin);
                server = NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.InOut,
                    admin ? 4 : 12,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                    64 * 1024,
                    64 * 1024,
                    security);
                await server.WaitForConnectionAsync(ct);
                _ = HandleClientAsync(server, admin, _cts?.Token ?? ct);
                server = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                Log.Warning(ex, "IPC 接受连接失败 {Pipe}", pipeName);
                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, bool admin, CancellationToken ct)
    {
        using (server)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var requestId = "unknown";
            try
            {
                var identity = GetClientIdentity(server);
                var line = await ReadLimitedLineAsync(server, timeout.Token);
                if (string.IsNullOrWhiteSpace(line))
                    return;

                IpcRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options);
                }
                catch (JsonException)
                {
                    await WriteResponseAsync(server, Failure("unknown", "IPC 请求格式无效。"), timeout.Token);
                    return;
                }
                if (request is null || string.IsNullOrWhiteSpace(request.Operation))
                {
                    await WriteResponseAsync(server, Failure("unknown", "IPC 请求为空。"), timeout.Token);
                    return;
                }
                requestId = request.Id;

                if (admin)
                {
                    if (!identity.IsAdmin)
                    {
                        await WriteResponseAsync(server, Failure(request.Id, "管理 IPC 需要管理员身份。"), timeout.Token);
                        return;
                    }
                    if (!_tokenStore.Validate(request.Token))
                    {
                        await WriteResponseAsync(server, Failure(request.Id, "IPC 令牌校验失败。"), timeout.Token);
                        return;
                    }
                    var payload = await DispatchAdminAsync(request, timeout.Token);
                    await WriteResponseAsync(server, Success(request.Id, payload), timeout.Token);
                }
                else
                {
                    if (!IsBrokerOperation(request.Operation))
                    {
                        await WriteResponseAsync(server, Failure(request.Id, "该操作不允许通过启动拦截通道执行。"), timeout.Token);
                        return;
                    }
                    var sidKey = identity.Sid?.Value ?? identity.UserName ?? "unknown";
                    if (!AllowBrokerRequest(sidKey))
                    {
                        await WriteResponseAsync(server, Failure(request.Id, "请求过于频繁。"), timeout.Token);
                        return;
                    }
                    var payload = await DispatchBrokerAsync(request, identity, timeout.Token);
                    await WriteResponseAsync(server, Success(request.Id, payload), timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Timed-out client.
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理 IPC 请求失败 {RequestId}", requestId);
                try
                {
                    await WriteResponseAsync(server, Failure(requestId, "服务处理请求时发生错误。"), CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private async Task<JsonElement> DispatchAdminAsync(IpcRequest request, CancellationToken ct)
    {
        switch (request.Operation)
        {
            case IpcOperations.Ping:
                return IpcJson.ToElement(new
                {
                    version = ServiceRuntimeState.Version,
                    startedAt = _state.StartedAt,
                    monitoring = _state.MonitoringActive,
                    ipc = _state.IpcActive
                });

            case IpcOperations.GetDashboard:
            {
                var settings = await _repository.GetSettingsAsync(ct);
                var summary = await _repository.GetDashboardSummaryAsync(ct);
                var recent = await _repository.GetRecentRecordsAsync(8, ct);
                var shellState = _shell.GetState();
                var targetRoot = settings.TargetDrive.EndsWith(':') ? settings.TargetDrive + "\\" : settings.TargetDrive;
                return IpcJson.ToElement(new DashboardSnapshot
                {
                    SystemDisk = _diskProbe.GetDiskInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"),
                    TargetDisk = _diskProbe.GetDiskInfo(targetRoot),
                    AutomaticRedirectEnabled = settings.AutomaticRedirectEnabled,
                    MonitoringActive = _state.MonitoringActive && _monitor.IsRunning,
                    ServiceOnline = true,
                    ServiceVersion = ServiceRuntimeState.Version,
                    TodayInstalls = summary.TodayInstalls,
                    TotalRedirects = summary.TotalRedirects,
                    TodayRedirects = summary.TodayRedirects,
                    ShellIntegration = shellState.State,
                    ShellIntegrationDetail = shellState.Detail,
                    RecentInstallations = recent
                });
            }

            case IpcOperations.GetLiveSessions:
                return IpcJson.ToElement(await _repository.GetLiveSessionsAsync(ct));

            case IpcOperations.GetHistory:
            {
                var query = IpcJson.Deserialize<HistoryQuery>(request.Payload) ?? new HistoryQuery();
                query = new HistoryQuery
                {
                    Search = query.Search,
                    ResultFilter = query.ResultFilter,
                    InstallerFilter = query.InstallerFilter,
                    SortBy = query.SortBy,
                    Descending = query.Descending,
                    Skip = Math.Max(0, query.Skip),
                    Take = Math.Clamp(query.Take, 1, 500)
                };
                return IpcJson.ToElement(await _repository.QueryHistoryAsync(query, ct));
            }

            case IpcOperations.ClearHistory:
                return IpcJson.ToElement(ServiceResult.Ok(
                    $"已清除 {await _repository.ClearHistoryAsync(ct)} 条已结束安装记录。"));

            case IpcOperations.GetRules:
                return IpcJson.ToElement(await _repository.GetRulesAsync(ct));

            case IpcOperations.UpsertRule:
            {
                var rule = IpcJson.Deserialize<PathRule>(request.Payload)
                           ?? throw new InvalidOperationException("规则内容为空。");
                if (rule.Id == Guid.Empty)
                    rule.Id = Guid.NewGuid();
                return IpcJson.ToElement(await _repository.UpsertRuleAsync(rule, ct));
            }

            case IpcOperations.DeleteRule:
            {
                var payload = IpcJson.Deserialize<DeleteRuleRequest>(request.Payload)
                              ?? throw new InvalidOperationException("删除规则请求为空。");
                var deleted = await _repository.DeleteRuleAsync(payload.Id, ct);
                return IpcJson.ToElement(deleted
                    ? ServiceResult.Ok("规则已删除。")
                    : ServiceResult.Fail("内置规则不可删除，或规则不存在。"));
            }

            case IpcOperations.GetCompatibility:
            {
                var observations = await _repository.GetObservationSummaryAsync(ct);
                return IpcJson.ToElement(_adapters.GetCompatibilityItems(observations));
            }

            case IpcOperations.GetObservedInstallers:
                return IpcJson.ToElement(await _repository.GetObservationsAsync(200, ct));

            case IpcOperations.GetSettings:
                return IpcJson.ToElement(await _repository.GetSettingsAsync(ct));

            case IpcOperations.SaveSettings:
                return IpcJson.ToElement(await SaveSettingsAsync(request, ct));

            case IpcOperations.GetDiagnostics:
                return IpcJson.ToElement(await BuildDiagnosticsAsync(ct));

            case IpcOperations.ExportLogs:
                return IpcJson.ToElement(ExportLogs(request));

            case IpcOperations.GetLogTail:
            {
                var tail = IpcJson.Deserialize<LogTailRequest>(request.Payload) ?? new LogTailRequest();
                return IpcJson.ToElement(ReadLogTail(Math.Clamp(tail.Lines, 20, 2000)));
            }

            case IpcOperations.BackupDatabase:
            {
                var path = await _repository.BackupDatabaseAsync(null, ct);
                return IpcJson.ToElement(ServiceResult.Ok(path));
            }

            case IpcOperations.SetShellIntegration:
            {
                var payload = IpcJson.Deserialize<SetShellIntegrationRequest>(request.Payload)
                              ?? throw new InvalidOperationException("关联设置请求为空。");
                var result = payload.Enabled ? _shell.Enable() : _shell.Disable();
                if (result.Success)
                {
                    var settings = await _repository.GetSettingsAsync(ct);
                    settings.ExplorerLaunchInterception = payload.Enabled;
                    await _repository.SaveSettingsAsync(settings, ct);
                }
                return IpcJson.ToElement(result);
            }

            default:
                throw new InvalidOperationException($"未知 IPC 操作：{request.Operation}");
        }
    }

    private async Task<JsonElement> DispatchBrokerAsync(IpcRequest request, ClientIdentity identity, CancellationToken ct)
    {
        switch (request.Operation)
        {
            case IpcOperations.PlanLaunch:
            {
                var planRequest = IpcJson.Deserialize<LaunchPlanRequest>(request.Payload)
                                  ?? throw new InvalidOperationException("启动计划请求为空。");
                var result = await _planner.PlanLaunchAsync(planRequest, identity.Sid?.Value, ct);
                return IpcJson.ToElement(result.Directive);
            }

            case IpcOperations.ReportProcessStarted:
            {
                var started = IpcJson.Deserialize<ProcessStartedRequest>(request.Payload)
                              ?? throw new InvalidOperationException("进程关联请求为空。");
                var ok = await _repository.MarkProcessStartedAsync(started.SessionId, started.ProcessId, ct);
                return IpcJson.ToElement(new { success = ok });
            }

            default:
                throw new InvalidOperationException("不支持的启动拦截操作。");
        }
    }

    private async Task<ServiceResult> SaveSettingsAsync(IpcRequest request, CancellationToken ct)
    {
        var incoming = IpcJson.Deserialize<AppSettings>(request.Payload)
                       ?? throw new InvalidOperationException("设置内容为空。");
        var normalizedDrive = incoming.TargetDrive.Trim().TrimEnd('\\').ToUpperInvariant();
        if (normalizedDrive != "D:")
            return ServiceResult.Fail("当前版本固定使用 D 盘，不能切换目标磁盘。");
        incoming.TargetDrive = "D:";
        incoming.MinimumFreeSpaceGb = Math.Clamp(incoming.MinimumFreeSpaceGb, 1, 1000);
        incoming.HistoryRetentionDays = Math.Clamp(incoming.HistoryRetentionDays, 0, 3650);

        var previous = await _repository.GetSettingsAsync(ct);
        await _repository.SaveSettingsAsync(incoming, ct);
        Program.LevelSwitch.MinimumLevel = Enum.TryParse<Serilog.Events.LogEventLevel>(
            incoming.LogLevel, true, out var level)
            ? level
            : Serilog.Events.LogEventLevel.Information;
        var notes = new List<string>();

        if (previous.ExplorerLaunchInterception != incoming.ExplorerLaunchInterception)
        {
            var shellResult = incoming.ExplorerLaunchInterception ? _shell.Enable() : _shell.Disable();
            if (!shellResult.Success)
            {
                incoming.ExplorerLaunchInterception = previous.ExplorerLaunchInterception;
                await _repository.SaveSettingsAsync(incoming, ct);
                return ServiceResult.Fail("应用启动拦截设置失败：" + shellResult.Detail);
            }
            notes.Add(shellResult.Detail);
        }

        if (previous.StartWithWindows != incoming.StartWithWindows)
        {
            var startup = ServiceStartupManager.SetAutomaticStart("DriveRedirect Service", incoming.StartWithWindows);
            if (!startup.Success)
                notes.Add("服务启动类型修改失败：" + startup.Error);
            else
                notes.Add(incoming.StartWithWindows ? "服务已设为自动启动。" : "服务已设为手动启动。");
        }

        if (previous.BackgroundMonitoringEnabled != incoming.BackgroundMonitoringEnabled)
            notes.Add("后台监控开关将在服务重启后完全生效。");

        return ServiceResult.Ok(notes.Count == 0 ? "设置已保存。" : string.Join(" ", notes));
    }

    private async Task<DiagnosticsSnapshot> BuildDiagnosticsAsync(CancellationToken ct)
    {
        var shellState = _shell.GetState();
        var exceptions = await _repository.GetExceptionSummaryAsync(ct);
        var logs = Directory.Exists(_paths.LogsDirectory)
            ? new DirectoryInfo(_paths.LogsDirectory)
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(20)
                .Select(f => new LogFileInfo
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    Length = f.Length,
                    LastWriteTime = f.LastWriteTime
                }).ToList()
            : [];
        return new DiagnosticsSnapshot
        {
            ServiceOnline = true,
            ServiceVersion = ServiceRuntimeState.Version,
            ServiceStartedAt = _state.StartedAt,
            DataDirectory = _paths.DataDirectory,
            DatabaseBytes = File.Exists(_paths.DatabasePath) ? new FileInfo(_paths.DatabasePath).Length : 0,
            LogsBytes = logs.Sum(l => l.Length),
            PendingSessions = await _repository.CountPendingSessionsAsync(ct),
            ExceptionCount24h = exceptions.Exceptions,
            LastError = exceptions.LastError,
            LogFiles = logs,
            ShellIntegration = shellState.State,
            ShellIntegrationDetail = shellState.Detail
        };
    }

    private ServiceResult ExportLogs(IpcRequest request)
    {
        var payload = IpcJson.Deserialize<ExportLogsRequest>(request.Payload) ?? new ExportLogsRequest();
        try
        {
            Directory.CreateDirectory(_paths.ExportDirectory);
            string destination;
            if (!string.IsNullOrWhiteSpace(payload.DestinationPath))
            {
                destination = Path.GetFullPath(payload.DestinationPath);
                if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    destination = Path.Combine(destination, $"DriveRedirect-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            }
            else
            {
                destination = Path.Combine(_paths.ExportDirectory, $"DriveRedirect-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            }

            if (File.Exists(destination))
                File.Delete(destination);
            using (var archive = ZipFile.Open(destination, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*", SearchOption.TopDirectoryOnly))
                    archive.CreateEntryFromFile(file, "logs/" + Path.GetFileName(file));
                var diagnostics = Path.Combine(Path.GetTempPath(), $"driveredirect-diagnostics-{Guid.NewGuid():N}.json");
                File.WriteAllText(diagnostics, JsonSerializer.Serialize(BuildDiagnosticsForExport(), IpcJson.Options));
                archive.CreateEntryFromFile(diagnostics, "diagnostics.json");
                File.Delete(diagnostics);
            }
            return ServiceResult.Ok(destination);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail("日志导出失败：" + ex.Message);
        }
    }

    private object BuildDiagnosticsForExport() => new
    {
        serviceVersion = ServiceRuntimeState.Version,
        startedAt = _state.StartedAt,
        dataDirectory = _paths.DataDirectory,
        generatedAt = DateTimeOffset.Now,
        shell = _shell.GetState()
    };

    private LogTailResult ReadLogTail(int lines)
    {
        var latest = Directory.Exists(_paths.LogsDirectory)
            ? new DirectoryInfo(_paths.LogsDirectory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest is null)
            return new LogTailResult { FileName = "", Content = "暂无日志。" };

        var queue = new Queue<string>();
        using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (reader.ReadLine() is { } line)
        {
            queue.Enqueue(line);
            if (queue.Count > lines)
                queue.Dequeue();
        }
        return new LogTailResult { FileName = latest.Name, Content = string.Join(Environment.NewLine, queue) };
    }

    private bool AllowBrokerRequest(string sid)
    {
        var now = DateTimeOffset.Now;
        var state = _brokerRates.GetOrAdd(sid, _ => new BrokerRate { WindowStart = now });
        lock (state)
        {
            if (now - state.WindowStart > TimeSpan.FromMinutes(1))
            {
                state.WindowStart = now;
                state.Count = 0;
            }
            state.Count++;
            return state.Count <= 120;
        }
    }

    private static bool IsBrokerOperation(string operation)
        => operation is IpcOperations.PlanLaunch or IpcOperations.ReportProcessStarted;

    private static ClientIdentity GetClientIdentity(NamedPipeServerStream server)
    {
        SecurityIdentifier? sid = null;
        string? userName = null;
        var isAdmin = false;
        try
        {
            server.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User;
                userName = identity.Name;
                isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch
        {
            try
            {
                userName = server.GetImpersonationUserName();
            }
            catch
            {
            }
        }
        return new ClientIdentity(sid, userName, isAdmin);
    }

    private static PipeSecurity CreatePipeSecurity(bool adminOnly)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!adminOnly)
        {
            var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            security.AddAccessRule(new PipeAccessRule(authenticated, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(network, PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private static async Task<string?> ReadLimitedLineAsync(Stream stream, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0)
                break;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            if (memory.Length + count > MaxRequestBytes)
                throw new InvalidOperationException("IPC 请求超过大小限制。");
            memory.Write(buffer, 0, count);
            if (newline >= 0)
                break;
        }
        if (memory.Length == 0)
            return null;
        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length).TrimEnd('\r');
    }

    private static async Task WriteResponseAsync(Stream stream, IpcResponse response, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(response, IpcJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
            json = JsonSerializer.Serialize(Failure(response.Id, "响应超过大小限制。"), IpcJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static IpcResponse Success(string id, JsonElement payload) => new()
    {
        Id = id,
        Success = true,
        Payload = payload
    };

    private static IpcResponse Failure(string id, string error) => new()
    {
        Id = id,
        Success = false,
        Error = error
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    private sealed class ClientIdentity(SecurityIdentifier? sid, string? userName, bool isAdmin)
    {
        public SecurityIdentifier? Sid { get; } = sid;
        public string? UserName { get; } = userName;
        public bool IsAdmin { get; } = isAdmin;
    }

    private sealed class BrokerRate
    {
        public DateTimeOffset WindowStart { get; set; }
        public int Count { get; set; }
    }
}

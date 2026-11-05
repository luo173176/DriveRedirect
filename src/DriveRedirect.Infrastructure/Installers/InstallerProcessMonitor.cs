using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Configuration;
using DriveRedirect.Infrastructure.Data;
using DriveRedirect.Infrastructure.Installers.Adapters;
using DriveRedirect.Infrastructure.Installers.Detection;
using Serilog;

namespace DriveRedirect.Infrastructure.Installers;

public sealed partial class InstallerProcessMonitor : IDisposable
{
    private readonly InstallerDetector _detector;
    private readonly AppRepository _repository;
    private readonly InstallVerificationService _verifier;
    private readonly AppPaths _paths;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, byte> _seenPids = new();
    private readonly ConcurrentDictionary<Guid, byte> _activeVerifications = new();
    private readonly CancellationTokenSource _cts = new();
    private ManagementEventWatcher? _watcher;
    private Task? _pollingTask;
    private Task? _sweepTask;
    private DateTimeOffset _startedAt;
    private bool _disposed;

    public InstallerProcessMonitor(
        InstallerDetector detector,
        AppRepository repository,
        InstallVerificationService verifier,
        AppPaths paths,
        ILogger logger)
    {
        _detector = detector;
        _repository = repository;
        _verifier = verifier;
        _paths = paths;
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
            return Task.CompletedTask;
        _startedAt = DateTimeOffset.Now;
        IsRunning = true;

        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2");
            scope.Connect();
            var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            _watcher = new ManagementEventWatcher(scope, query);
            _watcher.EventArrived += OnProcessStartTrace;
            _watcher.Start();
            _logger.Information("WMI 安装器进程启动监控已启动");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WMI 进程监控不可用，启用轮询后备");
        }

        _pollingTask = Task.Run(() => PollLoopAsync(_cts.Token));
        _sweepTask = Task.Run(() => SweepLoopAsync(_cts.Token));
        _ = Task.Run(() => ScanCurrentProcessesAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        _cts.Cancel();
        try
        {
            _watcher?.Stop();
        }
        catch
        {
            // Watcher may already be disposed.
        }
        _watcher?.Dispose();
        _watcher = null;
        await WaitIgnoringCancellation(_pollingTask);
        await WaitIgnoringCancellation(_sweepTask);
        _logger.Information("安装器进程监控已停止");
    }

    private static async Task WaitIgnoringCancellation(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void OnProcessStartTrace(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
            var name = e.NewEvent["ProcessName"] as string ?? "";
            if (pid <= 4 || pid == Environment.ProcessId)
                return;
            if (!_seenPids.TryAdd(pid, 0))
                return;
            _ = Task.Run(() => InspectProcessAsync(pid, name, _cts.Token));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "处理 WMI 进程启动事件失败");
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        var pid = process.Id;
                        if (_seenPids.ContainsKey(pid))
                            continue;
                        if (pid <= 4 || pid == Environment.ProcessId)
                            continue;
                        var name = process.ProcessName;
                        _seenPids.TryAdd(pid, 0);
                        _ = Task.Run(() => InspectProcessAsync(pid, name, ct), ct);
                    }
                }
                if (_seenPids.Count > 8192)
                {
                    var old = _seenPids.Keys.Take(_seenPids.Count - 4096).ToArray();
                    foreach (var pid in old)
                        _seenPids.TryRemove(pid, out _);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "进程轮询失败");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                var active = await _repository.GetActiveRecordsAsync(ct);
                foreach (var record in active)
                {
                    if (record.ProcessId is { } pid)
                    {
                        var alive = IsProcessAlive(pid);
                        if (!alive)
                            _ = CompleteAfterExitAsync(record, pid, ct);
                    }
                    else if (record.StartedAt < DateTimeOffset.Now.AddMinutes(-15))
                    {
                        record.Status = InstallationStatus.Interrupted;
                        record.EndedAt = DateTimeOffset.Now;
                        record.FailureReason = "启动拦截计划未确认到安装器进程，监控已安全结束。";
                        record.LastUpdatedAt = DateTimeOffset.Now;
                        await _repository.SaveRecordAsync(record, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "安装会话巡检失败");
            }
        }
    }

    private async Task ScanCurrentProcessesAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id <= 4 || process.Id == Environment.ProcessId)
                        continue;
                    _seenPids.TryAdd(process.Id, 0);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "初始进程扫描失败");
        }
    }

    private async Task InspectProcessAsync(int pid, string processName, CancellationToken ct)
    {
        try
        {
            var details = await QueryProcessDetailsAsync(pid, ct);
            if (details is null)
                return;
            var executable = details.Value.ExecutablePath;
            var commandLine = details.Value.CommandLine ?? "";
            if (string.IsNullOrWhiteSpace(executable))
                return;
            if (IsOwnProcess(executable))
                return;
            if (Core.Rules.WindowsPathTools.IsSystemProtectedPath(executable))
                return;

            string installerPath;
            if (Path.GetFileName(executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                var msi = ExtractMsiPath(commandLine);
                if (string.IsNullOrWhiteSpace(msi) || !File.Exists(msi))
                    return;
                if (Core.Rules.WindowsPathTools.IsSystemProtectedPath(msi))
                    return;
                installerPath = msi;
            }
            else
            {
                if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!Core.Installer.LaunchCandidateDetector.IsCandidate(executable, out _))
                    return;
                installerPath = executable;
            }

            var artifact = await _detector.AnalyzeForRecordingAsync(installerPath, ct);
            if (!artifact.IsInstaller && artifact.Type != InstallerType.Unknown)
                return;

            var activeAncestor = await FindActiveAncestorAsync(details.Value.ParentProcessId, ct);
            if (activeAncestor is not null &&
                !string.IsNullOrWhiteSpace(activeAncestor.InstallerPath) &&
                !string.Equals(
                    Path.GetFullPath(activeAncestor.InstallerPath),
                    Path.GetFullPath(installerPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug(
                    "安装器子进程 {Pid} {Path} 属于会话 {SessionId}，不创建重复记录",
                    pid,
                    installerPath,
                    activeAncestor.Id);
                return;
            }

            var pending = await _repository.FindPendingLaunchAsync(installerPath, DateTimeOffset.Now.AddMinutes(-20), ct);
            InstallationRecord record;
            if (pending is not null)
            {
                record = pending;
                if (record.ProcessId is null)
                {
                    record.ProcessId = pid;
                    record.Status = InstallationStatus.Running;
                    record.LastUpdatedAt = DateTimeOffset.Now;
                    await _repository.SaveRecordAsync(record, ct);
                }
                else if (record.ProcessId != pid)
                {
                    _logger.Debug("跳过重复安装器进程 {Pid}，会话已关联 {ExistingPid}", pid, record.ProcessId);
                    return;
                }
            }
            else
            {
                var softwareName = Core.Installer.InstallerNames.InferSoftwareName(
                    artifact.ProductName, artifact.FileDescription, artifact.FileName);
                var originalPath = artifact.Msi?.DefaultInstallPath ??
                                   AdapterHelpers.GuessProgramFilesDefault(artifact, softwareName);
                record = new InstallationRecord
                {
                    StartedAt = DateTimeOffset.Now,
                    InstallerType = artifact.Type,
                    SoftwareName = softwareName,
                    InstallerPath = artifact.FilePath,
                    InstallerVersion = artifact.ProductVersion,
                    InstallerHash = artifact.Sha256,
                    ProcessId = pid,
                    ParentProcessId = details.Value.ParentProcessId,
                    OriginalPath = originalPath,
                    Mode = HandlingMode.PassThrough,
                    Compatibility = artifact.Type == InstallerType.Unknown
                        ? CompatibilityLevel.Unsupported
                        : CompatibilityLevel.Medium,
                    Status = InstallationStatus.Running,
                    Strategy = "进程监控发现；启动前未被拦截，安全放行",
                    FailureReason = "进程已经启动，不能在写入文件前可靠改写路径。",
                    LastUpdatedAt = DateTimeOffset.Now
                };
                await _repository.SaveRecordAsync(record, ct);
                _logger.Warning("检测到未经过启动拦截的安装器 {Path} pid={Pid}，仅进行监控", installerPath, pid);
            }

            _ = Task.Run(() => WaitAndVerifyAsync(record, pid, ct), ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "检查进程 {Pid} {Name} 失败", pid, processName);
        }
    }

    private async Task WaitAndVerifyAsync(InstallationRecord record, int pid, CancellationToken ct)
    {
        if (!_activeVerifications.TryAdd(record.Id, 0))
            return;
        try
        {
            int? exitCode = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromHours(8));
                await process.WaitForExitAsync(timeout.Token);
                try
                {
                    exitCode = process.ExitCode;
                }
                catch
                {
                    exitCode = null;
                }
            }
            catch (ArgumentException)
            {
                // Process already exited; the verifier will use filesystem evidence.
            }
            catch (OperationCanceledException)
            {
                record.Status = InstallationStatus.Interrupted;
                record.EndedAt = DateTimeOffset.Now;
                record.FailureReason = "监控超时或服务关闭。";
                record.LastUpdatedAt = DateTimeOffset.Now;
                await _repository.SaveRecordAsync(record, CancellationToken.None);
                return;
            }

            var latest = await _repository.GetRecordAsync(record.Id, ct) ?? record;
            await _verifier.CompleteAsync(latest, exitCode, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "验证安装会话 {SessionId} 失败", record.Id);
        }
        finally
        {
            _activeVerifications.TryRemove(record.Id, out _);
        }
    }

    private Task CompleteAfterExitAsync(InstallationRecord record, int pid, CancellationToken ct)
        => WaitAndVerifyAsync(record, pid, ct);

    private bool IsOwnProcess(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (Core.Rules.WindowsPathTools.IsUnder(full, _paths.DataDirectory))
                return true;
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (Core.Rules.WindowsPathTools.IsUnder(full, baseDir))
                return true;
            var fileName = Path.GetFileName(full);
            return fileName.Equals("DriveRedirect.Service.exe", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("DriveRedirect.Desktop.exe", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("DriveRedirect.Launcher.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string? ExecutablePath, string? CommandLine, int ParentProcessId)?> QueryProcessDetailsAsync(
        int pid,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ExecutablePath, CommandLine, ParentProcessId FROM Win32_Process WHERE ProcessId={pid}");
                using var results = await Task.Run(() => searcher.Get(), ct);
                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        var path = item["ExecutablePath"] as string;
                        var commandLine = item["CommandLine"] as string;
                        var parent = item["ParentProcessId"] is null ? 0 : Convert.ToInt32(item["ParentProcessId"]);
                        if (!string.IsNullOrWhiteSpace(path))
                            return (path, commandLine, parent);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Process may have exited before WMI could resolve it.
            }
            await Task.Delay(150, ct);
        }
        return null;
    }

    private async Task<InstallationRecord?> FindActiveAncestorAsync(int parentProcessId, CancellationToken ct)
    {
        var pid = parentProcessId;
        for (var depth = 0; depth < 6 && pid > 4; depth++)
        {
            var record = await _repository.FindActiveRecordByProcessIdAsync(pid, ct);
            if (record is not null)
                return record;
            var details = await QueryProcessDetailsAsync(pid, ct);
            if (details is null || details.Value.ParentProcessId == pid)
                return null;
            pid = details.Value.ParentProcessId;
        }
        return null;
    }

    private static string? ExtractMsiPath(string commandLine)
    {
        var match = MsiCommandRegex().Match(commandLine);
        if (!match.Success)
            return null;
        try
        {
            return Path.GetFullPath(match.Groups["path"].Value.Trim('"'));
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"/i\s+(?<path>""[^""]+\.msi""|[^\s]+\.msi)", RegexOptions.IgnoreCase)]
    private static partial Regex MsiCommandRegex();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _watcher?.Stop();
        }
        catch
        {
        }
        _watcher?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}

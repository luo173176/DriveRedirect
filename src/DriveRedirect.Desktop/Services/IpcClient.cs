using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Desktop.Services;

public sealed class DriveRedirectServiceUnavailableException : Exception
{
    public DriveRedirectServiceUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class IpcClient
{
    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DriveRedirect",
        "ipc.token");

    private string? _token;

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await SendAsync<JsonElement>(IpcOperations.Ping, null, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<DashboardSnapshot> GetDashboardAsync(CancellationToken ct = default)
        => SendAsync<DashboardSnapshot>(IpcOperations.GetDashboard, null, ct);

    public Task<List<LiveInstallSession>> GetLiveSessionsAsync(CancellationToken ct = default)
        => SendAsync<List<LiveInstallSession>>(IpcOperations.GetLiveSessions, null, ct);

    public Task<HistoryResult> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default)
        => SendAsync<HistoryResult>(IpcOperations.GetHistory, query, ct);

    public Task<ServiceResult> ClearHistoryAsync(CancellationToken ct = default)
        => SendAsync<ServiceResult>(IpcOperations.ClearHistory, null, ct);

    public Task<List<PathRule>> GetRulesAsync(CancellationToken ct = default)
        => SendAsync<List<PathRule>>(IpcOperations.GetRules, null, ct);

    public Task<PathRule> UpsertRuleAsync(PathRule rule, CancellationToken ct = default)
        => SendAsync<PathRule>(IpcOperations.UpsertRule, rule, ct);

    public Task<ServiceResult> DeleteRuleAsync(Guid id, CancellationToken ct = default)
        => SendAsync<ServiceResult>(IpcOperations.DeleteRule, new DeleteRuleRequest { Id = id }, ct);

    public Task<List<CompatibilityItem>> GetCompatibilityAsync(CancellationToken ct = default)
        => SendAsync<List<CompatibilityItem>>(IpcOperations.GetCompatibility, null, ct);

    public Task<List<ObservedInstaller>> GetObservedInstallersAsync(CancellationToken ct = default)
        => SendAsync<List<ObservedInstaller>>(IpcOperations.GetObservedInstallers, null, ct);

    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
        => SendAsync<AppSettings>(IpcOperations.GetSettings, null, ct);

    public Task<ServiceResult> SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        => SendAsync<ServiceResult>(IpcOperations.SaveSettings, settings, ct);

    public Task<DiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken ct = default)
        => SendAsync<DiagnosticsSnapshot>(IpcOperations.GetDiagnostics, null, ct);

    public Task<ServiceResult> ExportLogsAsync(string? destinationPath, CancellationToken ct = default)
        => SendAsync<ServiceResult>(IpcOperations.ExportLogs, new ExportLogsRequest { DestinationPath = destinationPath }, ct);

    public Task<LogTailResult> GetLogTailAsync(int lines = 300, CancellationToken ct = default)
        => SendAsync<LogTailResult>(IpcOperations.GetLogTail, new LogTailRequest { Lines = lines }, ct);

    public Task<ServiceResult> BackupDatabaseAsync(CancellationToken ct = default)
        => SendAsync<ServiceResult>(IpcOperations.BackupDatabase, null, ct);

    public Task<ShellIntegrationResult> SetShellIntegrationAsync(bool enabled, CancellationToken ct = default)
        => SendAsync<ShellIntegrationResult>(IpcOperations.SetShellIntegration, new SetShellIntegrationRequest { Enabled = enabled }, ct);

    private async Task<T> SendAsync<T>(string operation, object? payload, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await SendOnceAsync<T>(operation, payload, token, ct);
            }
            catch (UnauthorizedAccessException) when (attempt == 0)
            {
                _token = null;
                token = await GetTokenAsync(ct);
            }
        }
        throw new DriveRedirectServiceUnavailableException("无法连接 DriveRedirect Service。");
    }

    private static async Task<T> SendOnceAsync<T>(
        string operation,
        object? payload,
        string token,
        CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            IpcNames.AdminPipe,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Impersonation);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
            await pipe.ConnectAsync(connectTimeout.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            throw new DriveRedirectServiceUnavailableException(
                "DriveRedirect Service 未运行或本地 IPC 不可用。", ex);
        }

        var requestId = Guid.NewGuid().ToString("D");
        var request = new IpcRequest
        {
            Id = requestId,
            Operation = operation,
            Token = token,
            Payload = payload is null ? null : IpcJson.ToElement(payload)
        };
        var requestJson = JsonSerializer.Serialize(request, IpcJson.Options);
        var requestBytes = Encoding.UTF8.GetBytes(requestJson + "\n");
        await pipe.WriteAsync(requestBytes, ct);
        await pipe.FlushAsync(ct);

        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(readTimeout.Token);
        if (string.IsNullOrWhiteSpace(responseLine))
            throw new DriveRedirectServiceUnavailableException("DriveRedirect Service 未返回响应。");

        var response = JsonSerializer.Deserialize<IpcResponse>(responseLine, IpcJson.Options)
                       ?? throw new DriveRedirectServiceUnavailableException("DriveRedirect Service 响应格式无效。");
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "服务操作失败。");
        return IpcJson.Deserialize<T>(response.Payload)
               ?? throw new InvalidOperationException("服务响应内容为空。");
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_token))
            return _token;
        if (!File.Exists(TokenPath))
            throw new DriveRedirectServiceUnavailableException("IPC 令牌不存在，后台服务尚未完成初始化。");
        _token = (await File.ReadAllTextAsync(TokenPath, ct)).Trim();
        if (_token.Length == 0)
            throw new DriveRedirectServiceUnavailableException("IPC 令牌为空。");
        return _token;
    }
}

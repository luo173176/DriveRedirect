using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Configuration;
using DriveRedirect.Infrastructure.Data;
using DriveRedirect.Infrastructure.Installers;
using DriveRedirect.Infrastructure.Security;
using DriveRedirect.Infrastructure.Shell;
using Serilog;
using Serilog.Events;

namespace DriveRedirect.Service;

public sealed class DriveRedirectWorker : BackgroundService
{
    private readonly AppPaths _paths;
    private readonly SqliteDatabase _database;
    private readonly AppRepository _repository;
    private readonly IpcTokenStore _tokenStore;
    private readonly ShellIntegrationManager _shell;
    private readonly InstallerProcessMonitor _monitor;
    private readonly LocalIpcServer _ipc;
    private readonly ServiceRuntimeState _state;

    public DriveRedirectWorker(
        AppPaths paths,
        SqliteDatabase database,
        AppRepository repository,
        IpcTokenStore tokenStore,
        ShellIntegrationManager shell,
        InstallerProcessMonitor monitor,
        LocalIpcServer ipc,
        ServiceRuntimeState state)
    {
        _paths = paths;
        _database = database;
        _repository = repository;
        _tokenStore = tokenStore;
        _shell = shell;
        _monitor = monitor;
        _ipc = ipc;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _database.EnsureInitializedAsync(stoppingToken);
        await _repository.MarkInterruptedAsync(stoppingToken);
        await _tokenStore.GetOrCreateAsync(stoppingToken);
        var settings = await _repository.GetSettingsAsync(stoppingToken);
        Program.LevelSwitch.MinimumLevel = ParseLevel(settings.LogLevel);

        if (settings.ExplorerLaunchInterception)
        {
            var shellResult = _shell.Enable();
            if (!shellResult.Success)
                Log.Warning("资源管理器启动拦截未启用：{Detail}", shellResult.Detail);
            else
                Log.Information("资源管理器启动拦截已启用：{Detail}", shellResult.Detail);
        }

        if (settings.BackgroundMonitoringEnabled)
        {
            await _monitor.StartAsync(stoppingToken);
            _state.MonitoringActive = _monitor.IsRunning;
        }
        else
        {
            Log.Warning("后台进程监控在设置中被禁用");
        }

        await _ipc.StartAsync(stoppingToken);
        _state.IpcActive = true;
        Log.Information("本地 IPC 已启动");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("DriveRedirect Service 正在停止");
        await _ipc.StopAsync(cancellationToken);
        await _monitor.StopAsync(cancellationToken);
        _state.MonitoringActive = false;
        _state.IpcActive = false;
        await base.StopAsync(cancellationToken);
        Log.Information("DriveRedirect Service 已停止");
    }

    private static LogEventLevel ParseLevel(string? value)
        => Enum.TryParse<LogEventLevel>(value, true, out var level) ? level : LogEventLevel.Information;
}

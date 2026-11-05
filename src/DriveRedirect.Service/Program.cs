using System.Reflection;
using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Configuration;
using DriveRedirect.Infrastructure.Data;
using DriveRedirect.Infrastructure.Installers;
using DriveRedirect.Infrastructure.Installers.Adapters;
using DriveRedirect.Infrastructure.Installers.Detection;
using DriveRedirect.Infrastructure.Security;
using DriveRedirect.Infrastructure.Shell;
using DriveRedirect.Infrastructure.Windows;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DriveRedirect.Service;

public static class Program
{
    public static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    public static async Task<int> Main(string[] args)
    {
        var paths = new AppPaths();
        paths.EnsureDirectories();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "service-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "未处理的托管异常");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "未观察的异步任务异常");
            e.SetObserved();
        };

        try
        {
            Log.Information("DriveRedirect Service {Version} 启动，数据目录 {DataDirectory}",
                typeof(Program).Assembly.GetName().Version,
                paths.DataDirectory);

            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "DriveRedirect Service";
            });
            builder.Services.AddSingleton(Log.Logger);
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<SqliteDatabase>();
            builder.Services.AddSingleton<AppRepository>();
            builder.Services.AddSingleton<WindowsDiskProbe>();
            builder.Services.AddSingleton<Core.Models.IDiskProbe>(
                sp => sp.GetRequiredService<WindowsDiskProbe>());
            builder.Services.AddSingleton<MsiNativeReader>();
            builder.Services.AddSingleton<MsiTransformBuilder>();
            builder.Services.AddSingleton<InstallerDetector>();
            builder.Services.AddSingleton<InstallerProfileStore>();
            builder.Services.AddSingleton<AdapterRegistry>();
            builder.Services.AddSingleton<Core.Rules.PathRedirectionEngine>();
            builder.Services.AddSingleton<RedirectionPlanner>();
            builder.Services.AddSingleton<ShellIntegrationManager>();
            builder.Services.AddSingleton<IpcTokenStore>();
            builder.Services.AddSingleton<InstallVerificationService>();
            builder.Services.AddSingleton<InstallerProcessMonitor>();
            builder.Services.AddSingleton<ServiceRuntimeState>();
            builder.Services.AddSingleton<LocalIpcServer>();
            builder.Services.AddHostedService<DriveRedirectWorker>();

            using var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "服务启动失败");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}

public sealed class ServiceRuntimeState
{
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public volatile bool MonitoringActive;
    public volatile bool IpcActive;
}

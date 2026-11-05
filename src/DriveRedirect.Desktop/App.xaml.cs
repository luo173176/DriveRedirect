using System.IO;
using System.Windows;
using System.Windows.Threading;
using DriveRedirect.Desktop.Services;
using DriveRedirect.Desktop.ViewModels;

namespace DriveRedirect.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog(args.ExceptionObject as Exception);
        };

        ThemeService.Apply(ThemeService.LoadLocalTheme());
        var ipc = new IpcClient();
        var serviceAdmin = new ServiceAdmin();
        var viewModel = new MainViewModel(ipc, serviceAdmin);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        _ = viewModel.InitializeAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            "DriveRedirect 遇到未处理错误：\n\n" + e.Exception.Message,
            "DriveRedirect",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null)
            return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DriveRedirect");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "desktop-crash.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

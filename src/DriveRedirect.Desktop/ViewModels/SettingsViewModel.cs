using System.Diagnostics;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveRedirect.Core.Models;
using DriveRedirect.Desktop.Services;
using Microsoft.Win32;

namespace DriveRedirect.Desktop.ViewModels;

public partial class SettingsViewModel : PageViewModelBase
{
    private readonly ServiceAdmin _serviceAdmin;
    private bool _loading;

    public SettingsViewModel(IpcClient ipc, ServiceAdmin serviceAdmin) : base(ipc)
    {
        _serviceAdmin = serviceAdmin;
        Themes =
        [
            new OptionItem("跟随系统", "System"),
            new OptionItem("浅色", "Light"),
            new OptionItem("深色", "Dark")
        ];
        LogLevels =
        [
            new OptionItem("详细", "Verbose"),
            new OptionItem("调试", "Debug"),
            new OptionItem("信息", "Information"),
            new OptionItem("警告", "Warning"),
            new OptionItem("错误", "Error")
        ];
        _selectedTheme = Themes[0];
        _selectedLogLevel = LogLevels[2];
    }

    public IReadOnlyList<OptionItem> Themes { get; }
    public IReadOnlyList<OptionItem> LogLevels { get; }
    public string AppVersion => "DriveRedirect " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    public string RuntimeText => $"{Environment.OSVersion.VersionString} · .NET {Environment.Version}";
    public string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DriveRedirect");

    [ObservableProperty] private bool _automaticRedirect;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _backgroundMonitoring;
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private bool _explorerIntegration;
    [ObservableProperty] private bool _enableMsiTransformFallback;
    [ObservableProperty] private bool _allowAppDataRedirection;
    [ObservableProperty] private bool _verifyAfterInstall;
    [ObservableProperty] private int _minimumFreeSpaceGb = 2;
    [ObservableProperty] private OptionItem _selectedTheme;
    [ObservableProperty] private OptionItem _selectedLogLevel;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _serviceStatus = "正在查询";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _diagnosticsSummary;
    [ObservableProperty] private string? _lastError;

    public string TargetDriveText => "D:（固定）";
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasLastError => !string.IsNullOrWhiteSpace(LastError);

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));
    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(HasLastError));

    partial void OnSelectedThemeChanged(OptionItem value)
    {
        if (_loading || value is null)
            return;
        ThemeService.Apply(value.Value);
        ThemeService.SaveLocalTheme(value.Value);
    }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        _loading = true;
        try
        {
            var settings = await Ipc.GetSettingsAsync(ct);
            AutomaticRedirect = settings.AutomaticRedirectEnabled;
            StartWithWindows = settings.StartWithWindows;
            BackgroundMonitoring = settings.BackgroundMonitoringEnabled;
            NotificationsEnabled = settings.NotificationsEnabled;
            ExplorerIntegration = settings.ExplorerLaunchInterception;
            EnableMsiTransformFallback = settings.EnableMsiTransformFallback;
            AllowAppDataRedirection = settings.AllowAppDataRedirection;
            VerifyAfterInstall = settings.VerifyAfterInstall;
            MinimumFreeSpaceGb = settings.MinimumFreeSpaceGb;
            SelectedTheme = Themes.FirstOrDefault(t => t.Value == settings.Theme) ?? Themes[0];
            SelectedLogLevel = LogLevels.FirstOrDefault(l => l.Value == settings.LogLevel) ?? LogLevels[2];

            var status = await _serviceAdmin.GetStatusAsync(ct);
            ServiceStatus = status.Display;
            var diagnostics = await Ipc.GetDiagnosticsAsync(ct);
            DiagnosticsSummary =
                $"数据库 {FormatBytes(diagnostics.DatabaseBytes)} · 日志 {FormatBytes(diagnostics.LogsBytes)} · " +
                $"待完成会话 {diagnostics.PendingSessions} · 24h 异常 {diagnostics.ExceptionCount24h}";
            LastError = diagnostics.LastError;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            var settings = new AppSettings
            {
                AutomaticRedirectEnabled = AutomaticRedirect,
                BackgroundMonitoringEnabled = BackgroundMonitoring,
                StartWithWindows = StartWithWindows,
                NotificationsEnabled = NotificationsEnabled,
                ExplorerLaunchInterception = ExplorerIntegration,
                EnableMsiTransformFallback = EnableMsiTransformFallback,
                AllowAppDataRedirection = AllowAppDataRedirection,
                VerifyAfterInstall = VerifyAfterInstall,
                MinimumFreeSpaceGb = Math.Clamp(MinimumFreeSpaceGb, 1, 1000),
                TargetDrive = "D:",
                Theme = SelectedTheme.Value,
                LogLevel = SelectedLogLevel.Value
            };
            var result = await Ipc.SaveSettingsAsync(settings);
            StatusMessage = result.Message;
            ThemeService.Apply(SelectedTheme.Value);
            ThemeService.SaveLocalTheme(SelectedTheme.Value);
            if (!result.Success)
                ErrorMessage = result.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = "保存设置失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            var result = await _serviceAdmin.RestartAsync();
            StatusMessage = result.Message;
            await Task.Delay(800);
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 DriveRedirect 日志",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"DriveRedirect-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var result = await Ipc.ExportLogsAsync(dialog.FileName);
            StatusMessage = result.Message ?? "日志已导出。";
        }
        catch (Exception ex)
        {
            ErrorMessage = "导出日志失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task BackupDatabaseAsync()
    {
        try
        {
            var result = await Ipc.BackupDatabaseAsync();
            StatusMessage = "数据库备份已创建：" + result.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = "备份数据库失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (MessageBox.Show("确定清空安装历史吗？此操作不会删除软件或日志。",
                "清空安装历史", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = await Ipc.ClearHistoryAsync();
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = "清空历史失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", DataDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var path = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task RestoreShellAssociationAsync()
    {
        if (MessageBox.Show(
                "将关闭资源管理器 EXE/MSI 双击接管并恢复原始关联。自动重定向将只对经过其他入口启动的安装器生效，确定继续吗？",
                "恢复启动关联",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = await Ipc.SetShellIntegrationAsync(false);
            ExplorerIntegration = false;
            StatusMessage = result.Detail;
            if (!result.Success)
                ErrorMessage = result.Detail;
        }
        catch (Exception ex)
        {
            ErrorMessage = "恢复启动关联失败：" + ex.Message;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.#} {units[index]}";
    }
}

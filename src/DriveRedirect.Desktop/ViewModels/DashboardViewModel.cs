using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveRedirect.Core.Models;
using DriveRedirect.Desktop.Services;

namespace DriveRedirect.Desktop.ViewModels;

public partial class DashboardViewModel : PageViewModelBase
{
    public DashboardViewModel(IpcClient ipc) : base(ipc)
    {
    }

    public ObservableCollection<InstallationRecord> RecentInstallations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemFreeText), nameof(SystemTotalText), nameof(SystemUsagePercent), nameof(SystemUsageText))]
    private DiskInfo? _systemDisk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFreeText), nameof(TargetTotalText), nameof(TargetUsagePercent), nameof(TargetUsageText), nameof(TargetDiskReady))]
    private DiskInfo? _targetDisk;

    [ObservableProperty]
    private bool _automaticRedirectEnabled;

    [ObservableProperty]
    private bool _monitoringActive;

    [ObservableProperty]
    private int _todayInstalls;

    [ObservableProperty]
    private int _todayRedirects;

    [ObservableProperty]
    private int _totalRedirects;

    [ObservableProperty]
    private string _shellIntegrationText = "未启用";

    [ObservableProperty]
    private string? _shellIntegrationDetail;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string? _statusMessage;

    public string SystemFreeText => FormatBytes(SystemDisk?.FreeBytes ?? 0);
    public string SystemTotalText => $"总容量 {FormatBytes(SystemDisk?.TotalBytes ?? 0)}";
    public double SystemUsagePercent => SystemDisk?.UsagePercent ?? 0;
    public string SystemUsageText => SystemDisk?.IsReady == false ? "磁盘未就绪" : $"{SystemUsagePercent:0.#}% 已使用";
    public string TargetFreeText => FormatBytes(TargetDisk?.FreeBytes ?? 0);
    public string TargetTotalText => $"总容量 {FormatBytes(TargetDisk?.TotalBytes ?? 0)}";
    public double TargetUsagePercent => TargetDisk?.UsagePercent ?? 0;
    public string TargetUsageText => TargetDisk?.IsReady == false ? "D 盘不可用" : $"{TargetUsagePercent:0.#}% 已使用";
    public bool TargetDiskReady => TargetDisk?.IsReady == true;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasRecent => RecentInstallations.Count > 0;

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var data = await Ipc.GetDashboardAsync(ct);
        AutomaticRedirectEnabled = data.AutomaticRedirectEnabled;
        MonitoringActive = data.MonitoringActive;
        TodayInstalls = data.TodayInstalls;
        TodayRedirects = data.TodayRedirects;
        TotalRedirects = data.TotalRedirects;
        SystemDisk = data.SystemDisk;
        TargetDisk = data.TargetDisk;
        ShellIntegrationDetail = data.ShellIntegrationDetail;
        ShellIntegrationText = data.ShellIntegration switch
        {
            ShellIntegrationState.Enabled => "资源管理器接管已启用",
            ShellIntegrationState.Partial => "资源管理器接管部分启用",
            ShellIntegrationState.Unavailable => "资源管理器接管不可用",
            _ => "资源管理器接管未启用"
        };
        RecentInstallations.Clear();
        foreach (var record in data.RecentInstallations)
            RecentInstallations.Add(record);
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand]
    private async Task ToggleAutoRedirectAsync()
    {
        if (IsSaving)
            return;
        IsSaving = true;
        try
        {
            var settings = await Ipc.GetSettingsAsync();
            settings.AutomaticRedirectEnabled = !AutomaticRedirectEnabled;
            var result = await Ipc.SaveSettingsAsync(settings);
            if (!result.Success)
            {
                StatusMessage = result.Message;
                return;
            }
            AutomaticRedirectEnabled = settings.AutomaticRedirectEnabled;
            StatusMessage = settings.AutomaticRedirectEnabled
                ? "自动重定向已开启。新的可识别安装器将优先进入 D 盘。"
                : "自动重定向已关闭。安装器会被监控但不会改写路径。";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DriveRedirect",
            "logs");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DriveRedirect");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        if (bytes < 1024)
            return $"{bytes} B";
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.#} {units[index]}";
    }
}

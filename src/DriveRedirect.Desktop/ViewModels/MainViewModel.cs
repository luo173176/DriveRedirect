using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveRedirect.Core.Models;
using DriveRedirect.Desktop.Services;

namespace DriveRedirect.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IpcClient _ipc;
    private readonly ServiceAdmin _serviceAdmin;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _notifiedSessions = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastNotificationPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSettingsPoll = DateTimeOffset.MinValue;
    private bool _notificationsEnabled = true;
    private bool _notificationSeeded;
    private bool _lastOnline;

    public MainViewModel(IpcClient ipc, ServiceAdmin serviceAdmin)
    {
        _ipc = ipc;
        _serviceAdmin = serviceAdmin;
        var dashboard = new DashboardViewModel(ipc);
        var live = new LiveViewModel(ipc);
        var history = new HistoryViewModel(ipc);
        var rules = new RulesViewModel(ipc);
        var compatibility = new CompatibilityViewModel(ipc);
        var settings = new SettingsViewModel(ipc, serviceAdmin);

        Pages = new ObservableCollection<PageViewModelBase>
        {
            dashboard, live, history, rules, compatibility, settings
        };
        NavigationItems =
        [
            new NavigationItem("home", "首页", "\uE80F", dashboard),
            new NavigationItem("live", "实时监控", "\uE7F4", live),
            new NavigationItem("history", "安装记录", "\uE81C", history),
            new NavigationItem("rules", "路径规则", "\uE8B7", rules),
            new NavigationItem("compatibility", "兼容性中心", "\uE73E", compatibility),
            new NavigationItem("settings", "设置", "\uE713", settings)
        ];
        CurrentPage = dashboard;
        SelectedNavigation = NavigationItems[0];
        ServiceStatusText = "正在连接后台服务";
        ServiceStatusDetail = "等待 DriveRedirect Service 响应";
        AppVersion = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }
    public ObservableCollection<PageViewModelBase> Pages { get; }
    public string AppVersion { get; }

    [ObservableProperty]
    private NavigationItem _selectedNavigation;

    [ObservableProperty]
    private PageViewModelBase _currentPage;

    [ObservableProperty]
    private bool _isServiceOnline;

    [ObservableProperty]
    private string _serviceStatusText;

    [ObservableProperty]
    private string _serviceStatusDetail;

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastMessage = "";

    public string ThemeGlyph => ThemeService.IsDark ? "\uE706" : "\uE708";
    public string ThemeToolTip => ThemeService.IsDark ? "切换到浅色模式" : "切换到深色模式";

    partial void OnSelectedNavigationChanged(NavigationItem value)
    {
        if (value is null)
            return;
        CurrentPage = value.Page;
        _ = CurrentPage.RefreshAsync(_cts.Token);
    }

    public async Task InitializeAsync()
    {
        ThemeService.Apply(ThemeService.LoadLocalTheme());
        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeToolTip));
        try
        {
            var settings = await _ipc.GetSettingsAsync(_cts.Token);
            _notificationsEnabled = settings.NotificationsEnabled;
            ThemeService.Apply(settings.Theme);
            ThemeService.SaveLocalTheme(settings.Theme);
            OnPropertyChanged(nameof(ThemeGlyph));
            OnPropertyChanged(nameof(ThemeToolTip));
        }
        catch
        {
            // Local theme remains available while the service is offline.
        }

        await ConnectionTickAsync(first: true);
        _ = PollLoopAsync(_cts.Token);
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        ThemeService.ToggleLightDark();
        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeToolTip));
    }

    [RelayCommand]
    private async Task RefreshCurrent()
    {
        await CurrentPage.RefreshAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var ping = await _ipc.PingAsync(ct);
                var wasOnline = IsServiceOnline;
                IsServiceOnline = ping;
                if (ping)
                {
                    ServiceStatusText = "后台服务运行中";
                    ServiceStatusDetail = "安装监控与自动重定向已连接";
                    var page = CurrentPage;
                    if (!wasOnline || page is not null)
                        await page.RefreshAsync(ct);
                    await PollNotificationsAsync(ct);
                }
                else
                {
                    ServiceStatusText = "后台服务离线";
                    ServiceStatusDetail = "请以管理员身份启动 DriveRedirect Service";
                }
                _lastOnline = ping;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ConnectionTickAsync(bool first)
    {
        var online = await _ipc.PingAsync(_cts.Token);
        IsServiceOnline = online;
        ServiceStatusText = online ? "后台服务运行中" : "后台服务离线";
        ServiceStatusDetail = online ? "安装监控与自动重定向已连接" : "请以管理员身份启动 DriveRedirect Service";
        await CurrentPage.RefreshAsync(_cts.Token);
    }

    private async Task PollNotificationsAsync(CancellationToken ct)
    {
        if (DateTimeOffset.Now - _lastNotificationPoll < TimeSpan.FromSeconds(9))
            return;
        _lastNotificationPoll = DateTimeOffset.Now;

        if (DateTimeOffset.Now - _lastSettingsPoll > TimeSpan.FromMinutes(1))
        {
            try
            {
                var settings = await _ipc.GetSettingsAsync(ct);
                _notificationsEnabled = settings.NotificationsEnabled;
            }
            catch
            {
            }
            _lastSettingsPoll = DateTimeOffset.Now;
        }
        if (!_notificationsEnabled)
            return;

        try
        {
            var history = await _ipc.GetHistoryAsync(new HistoryQuery { Take = 25 }, ct);
            if (!_notificationSeeded)
            {
                foreach (var item in history.Items)
                    _notifiedSessions.Add(item.Id.ToString("D"));
                _notificationSeeded = true;
                return;
            }

            foreach (var record in history.Items.OrderBy(r => r.StartedAt))
            {
                var key = record.Id.ToString("D");
                if (!_notifiedSessions.Add(key))
                    continue;
                if (record.Status is InstallationStatus.Redirected or InstallationStatus.Succeeded
                    or InstallationStatus.Assisted or InstallationStatus.Failed
                    or InstallationStatus.Cancelled or InstallationStatus.Unverified)
                {
                    ShowToast(record.Status switch
                    {
                        InstallationStatus.Redirected => $"{record.SoftwareName} 已安装到 D 盘",
                        InstallationStatus.Succeeded or InstallationStatus.Assisted => $"{record.SoftwareName} 安装完成",
                        InstallationStatus.Failed => $"{record.SoftwareName} 安装失败",
                        _ => $"{record.SoftwareName} 安装结果待确认"
                    });
                }
            }
            if (_notifiedSessions.Count > 500)
                _notifiedSessions.Clear();
        }
        catch
        {
        }
    }

    public async void ShowToast(string message)
    {
        IsToastVisible = true;
        ToastMessage = message;
        var token = _cts.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4.5), token);
            if (ToastMessage == message)
                IsToastVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

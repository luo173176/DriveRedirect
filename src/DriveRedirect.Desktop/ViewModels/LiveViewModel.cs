using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DriveRedirect.Core.Models;
using DriveRedirect.Desktop.Services;

namespace DriveRedirect.Desktop.ViewModels;

public partial class LiveViewModel : PageViewModelBase
{
    public LiveViewModel(IpcClient ipc) : base(ipc)
    {
    }

    public ObservableCollection<LiveInstallSession> Sessions { get; } = [];

    [ObservableProperty]
    private LiveInstallSession? _currentSession;

    public bool HasSessions => Sessions.Count > 0;
    public bool IsEmpty => Sessions.Count == 0;
    public string SessionCountText => Sessions.Count == 0 ? "暂无活动安装" : $"{Sessions.Count} 个活动安装";

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var sessions = await Ipc.GetLiveSessionsAsync(ct);
        Sessions.Clear();
        foreach (var session in sessions)
            Sessions.Add(session);
        CurrentSession = Sessions.FirstOrDefault();
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SessionCountText));
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DriveRedirect.Core.Models;
using DriveRedirect.Desktop.Services;

namespace DriveRedirect.Desktop.ViewModels;

public partial class CompatibilityViewModel : PageViewModelBase
{
    public CompatibilityViewModel(IpcClient ipc) : base(ipc)
    {
    }

    public ObservableCollection<CompatibilityItem> Adapters { get; } = [];
    public ObservableCollection<ObservedInstaller> ObservedInstallers { get; } = [];

    [ObservableProperty]
    private string _lastUpdatedText = "";

    public bool HasObservations => ObservedInstallers.Count > 0;
    public bool HasNoObservations => ObservedInstallers.Count == 0;

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var adapters = await Ipc.GetCompatibilityAsync(ct);
        var observed = await Ipc.GetObservedInstallersAsync(ct);
        Adapters.Clear();
        foreach (var item in adapters)
            Adapters.Add(item);
        ObservedInstallers.Clear();
        foreach (var item in observed)
            ObservedInstallers.Add(item);
        LastUpdatedText = "最后刷新 " + DateTime.Now.ToString("HH:mm:ss");
        OnPropertyChanged(nameof(HasObservations));
        OnPropertyChanged(nameof(HasNoObservations));
    }
}

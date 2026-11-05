using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveRedirect.Desktop.Services;

namespace DriveRedirect.Desktop.ViewModels;

public abstract partial class PageViewModelBase : ObservableObject
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    protected PageViewModelBase(IpcClient ipc)
    {
        Ipc = ipc;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
    }

    protected IpcClient Ipc { get; }
    public IAsyncRelayCommand RefreshCommand { get; }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public virtual bool ShowRefreshButton => true;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!await _refreshGate.WaitAsync(0, ct))
            return;
        IsLoading = true;
        try
        {
            await LoadAsync(ct);
            ErrorMessage = null;
        }
        catch (DriveRedirectServiceUnavailableException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            _refreshGate.Release();
        }
    }

    protected abstract Task LoadAsync(CancellationToken ct);
}

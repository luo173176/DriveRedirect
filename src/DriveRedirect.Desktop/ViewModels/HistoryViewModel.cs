using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveRedirect.Core.Models;
using DriveRedirect.Desktop.Services;
using Microsoft.Win32;

namespace DriveRedirect.Desktop.ViewModels;

public partial class HistoryViewModel : PageViewModelBase
{
    private int _loadVersion;

    public HistoryViewModel(IpcClient ipc) : base(ipc)
    {
        ResultFilters =
        [
            new OptionItem("全部结果", "all"),
            new OptionItem("已重定向", "redirected"),
            new OptionItem("安全放行", "passed"),
            new OptionItem("失败 / 取消", "failed"),
            new OptionItem("进行中", "running")
        ];
        InstallerFilters =
        [
            new OptionItem("全部安装器", "all"),
            new OptionItem("Windows MSI", "WindowsMsi"),
            new OptionItem("WiX MSI", "Wix"),
            new OptionItem("Inno Setup", "InnoSetup"),
            new OptionItem("NSIS", "Nsis"),
            new OptionItem("InstallShield", "InstallShield"),
            new OptionItem("自定义 EXE", "CustomExe"),
            new OptionItem("MSIX / AppX", "Msix"),
            new OptionItem("游戏启动器", "GameLauncher"),
            new OptionItem("未知", "Unknown")
        ];
        SortOptions =
        [
            new OptionItem("安装时间", "date"),
            new OptionItem("软件名称", "name"),
            new OptionItem("安装器类型", "installer"),
            new OptionItem("安装结果", "status"),
            new OptionItem("实际路径", "target")
        ];
        _selectedResultFilter = ResultFilters[0];
        _selectedInstallerFilter = InstallerFilters[0];
        _selectedSort = SortOptions[0];
    }

    public ObservableCollection<InstallationRecord> Items { get; } = [];
    public IReadOnlyList<OptionItem> ResultFilters { get; }
    public IReadOnlyList<OptionItem> InstallerFilters { get; }
    public IReadOnlyList<OptionItem> SortOptions { get; }

    [ObservableProperty]
    private OptionItem _selectedResultFilter;

    [ObservableProperty]
    private OptionItem _selectedInstallerFilter;

    [ObservableProperty]
    private OptionItem _selectedSort;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    private InstallationRecord? _selectedRecord;

    [ObservableProperty]
    private string _totalText = "0 条记录";

    [ObservableProperty]
    private string? _statusMessage;

    public bool HasRecords => Items.Count > 0;
    public bool IsEmpty => Items.Count == 0;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasSelectedRecord => SelectedRecord is not null;

    partial void OnSelectedResultFilterChanged(OptionItem value) => ReloadFromFilter();
    partial void OnSelectedInstallerFilterChanged(OptionItem value) => ReloadFromFilter();
    partial void OnSelectedSortChanged(OptionItem value) => ReloadFromFilter();
    partial void OnSortDescendingChanged(bool value) => ReloadFromFilter();
    partial void OnSelectedRecordChanged(InstallationRecord? value) => OnPropertyChanged(nameof(HasSelectedRecord));
    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        var result = await Ipc.GetHistoryAsync(new HistoryQuery
        {
            Search = SearchText,
            ResultFilter = SelectedResultFilter?.Value,
            InstallerFilter = SelectedInstallerFilter?.Value,
            SortBy = SelectedSort?.Value ?? "date",
            Descending = SortDescending,
            Take = 250
        }, ct);
        if (version != _loadVersion)
            return;
        Items.Clear();
        foreach (var record in result.Items)
            Items.Add(record);
        TotalText = result.Total > Items.Count
            ? $"显示 {Items.Count} / 共 {result.Total} 条记录"
            : $"共 {result.Total} 条记录";
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private Task SearchAsync() => RefreshAsync();

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (MessageBox.Show(
                "确定清除全部已结束的安装记录吗？路径规则、适配器配置与日志不会受影响。",
                "清除安装记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = await Ipc.ClearHistoryAsync();
            StatusMessage = result.Message;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Items.Count == 0)
        {
            StatusMessage = "当前没有可导出的记录。";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "导出安装记录",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"DriveRedirect-history-{DateTime.Now:yyyyMMdd}.csv"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("软件名称,安装时间,原始路径,实际路径,安装器类型,处理模式,安装结果,是否重定向");
            foreach (var item in Items)
            {
                sb.Append(Csv(item.SoftwareName)).Append(',')
                    .Append(Csv(item.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                    .Append(Csv(item.OriginalPath ?? "")).Append(',')
                    .Append(Csv(item.ActualPath ?? item.TargetPath ?? "")).Append(',')
                    .Append(Csv(item.InstallerType.ToString())).Append(',')
                    .Append(Csv(item.Mode.ToString())).Append(',')
                    .Append(Csv(item.Status.ToString())).Append(',')
                    .Append(Csv(item.WasRedirected ? "是" : "否"))
                    .AppendLine();
            }
            await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            StatusMessage = "记录已导出到 " + dialog.FileName;
        }
        catch (Exception ex)
        {
            ErrorMessage = "导出失败：" + ex.Message;
        }
    }

    private void ReloadFromFilter()
    {
        if (IsLoading)
            return;
        _ = RefreshAsync();
    }

    private static string Csv(string value)
        => "\"" + value.Replace("\"", "\"\"") + "\"";
}

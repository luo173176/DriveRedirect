using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;
using DriveRedirect.Desktop.Dialogs;
using DriveRedirect.Desktop.Services;

namespace DriveRedirect.Desktop.ViewModels;

public partial class RulesViewModel : PageViewModelBase
{
    private List<PathRule> _allRules = [];

    public RulesViewModel(IpcClient ipc) : base(ipc)
    {
        Filters =
        [
            new OptionItem("全部规则", "all"),
            new OptionItem("路径映射", "mapping"),
            new OptionItem("应用白名单", "whitelist"),
            new OptionItem("应用黑名单", "blacklist"),
            new OptionItem("保护目录", "protected")
        ];
        _selectedFilter = Filters[0];
        ProtectedPaths = new ObservableCollection<string>(PathRedirectionEngine.GetDefaultProtectedPaths());
    }

    public ObservableCollection<PathRule> Rules { get; } = [];
    public ObservableCollection<string> ProtectedPaths { get; }
    public IReadOnlyList<OptionItem> Filters { get; }

    [ObservableProperty]
    private OptionItem _selectedFilter;

    [ObservableProperty]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool IsEmpty => Rules.Count == 0;
    public string RuleCountText => $"共 {Rules.Count} 条规则";

    partial void OnSelectedFilterChanged(OptionItem value) => ApplyFilter();
    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    protected override async Task LoadAsync(CancellationToken ct)
    {
        _allRules = (await Ipc.GetRulesAsync(ct)).ToList();
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ToggleRuleAsync(PathRule? rule)
    {
        if (rule is null)
            return;
        var previous = rule.Enabled;
        rule.Enabled = !rule.Enabled;
        try
        {
            await Ipc.UpsertRuleAsync(rule);
            StatusMessage = rule.Enabled ? $"已启用「{rule.Name}」。" : $"已停用「{rule.Name}」。";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            rule.Enabled = previous;
            ErrorMessage = "更新规则失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(PathRule? rule)
    {
        if (rule is null)
            return;
        if (rule.IsBuiltIn)
        {
            StatusMessage = "内置规则不可删除，只能停用或修改映射。";
            return;
        }
        if (MessageBox.Show(
                $"确定删除规则「{rule.Name}」吗？",
                "删除路径规则",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = await Ipc.DeleteRuleAsync(rule.Id);
            StatusMessage = result.Message;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = "删除规则失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var dialog = new AddRuleDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;
        try
        {
            await Ipc.UpsertRuleAsync(dialog.Result);
            StatusMessage = $"已添加规则「{dialog.Result.Name}」。";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = "添加规则失败：" + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        var kind = SelectedFilter?.Value switch
        {
            "mapping" => RuleKind.Mapping,
            "whitelist" => RuleKind.AppWhitelist,
            "blacklist" => RuleKind.AppBlacklist,
            "protected" => RuleKind.ProtectedPath,
            _ => (RuleKind?)null
        };
        var filtered = kind is null ? _allRules : _allRules.Where(r => r.Kind == kind).ToList();
        Rules.Clear();
        foreach (var rule in filtered.OrderByDescending(r => r.Priority).ThenBy(r => r.Name))
            Rules.Add(rule);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(RuleCountText));
    }
}

using System.Windows;
using System.Windows.Input;
using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;

namespace DriveRedirect.Desktop.Dialogs;

public partial class AddRuleDialog : Window
{
    public AddRuleDialog()
    {
        InitializeComponent();
    }

    public PathRule? Result { get; private set; }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void KindCombo_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        UpdateFields();
    }

    private void UpdateFields()
    {
        var kind = GetKind();
        var isMapping = kind == RuleKind.Mapping;
        TargetPanel.Visibility = isMapping ? Visibility.Visible : Visibility.Collapsed;
        AllowAppDataCheck.Visibility = isMapping || kind == RuleKind.AppWhitelist
            ? Visibility.Visible
            : Visibility.Collapsed;
        SourceLabel.Text = kind switch
        {
            RuleKind.Mapping => "C 盘源路径",
            RuleKind.AppWhitelist => "应用名称匹配（支持 * 通配符）",
            RuleKind.AppBlacklist => "应用名称或 C 盘路径",
            _ => "受保护目录路径"
        };
        SourceHint.Text = kind switch
        {
            RuleKind.Mapping => @"例如 C:\Program Files\Example",
            RuleKind.AppWhitelist => "例如 Acme*；须同时勾选“允许 AppData”并添加对应 AppData 映射。",
            RuleKind.AppBlacklist => @"例如 *GreenApp* 或 C:\Program Files\GreenApp",
            _ => @"例如 C:\Windows\System32"
        };
    }

    private void Add_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ValidationText.Visibility = Visibility.Collapsed;
            var kind = GetKind();
            var name = NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("请填写规则名称。");
            var source = SourceBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("请填写源路径或匹配模式。");
            string? target = null;
            if (kind == RuleKind.Mapping)
            {
                source = WindowsPathTools.NormalizeDirectory(source);
                target = WindowsPathTools.NormalizeDirectory(TargetBox.Text);
                if (!string.Equals(Path.GetPathRoot(target), @"D:\", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("路径映射目标必须位于 D 盘。");
            }
            else if (kind == RuleKind.ProtectedPath)
            {
                source = WindowsPathTools.NormalizeDirectory(source);
            }
            if (!int.TryParse(PriorityBox.Text.Trim(), out var priority))
                throw new ArgumentException("优先级必须是整数。");

            Result = new PathRule
            {
                Id = Guid.NewGuid(),
                Name = name,
                Kind = kind,
                SourcePattern = source,
                TargetPattern = target,
                Enabled = true,
                Priority = priority,
                AllowAppData = AllowAppDataCheck.IsChecked == true,
                Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim(),
                IsBuiltIn = false
            };
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ValidationText.Text = ex.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    private RuleKind GetKind()
    {
        if (KindCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            item.Tag is not string tag)
            return RuleKind.Mapping;
        return Enum.TryParse<RuleKind>(tag, out var kind) ? kind : RuleKind.Mapping;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Desktop.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is InstallationStatus status ? status switch
        {
            InstallationStatus.Planned => "已计划",
            InstallationStatus.Launching => "启动中",
            InstallationStatus.Running => "安装中",
            InstallationStatus.Succeeded => "安装成功",
            InstallationStatus.Redirected => "已重定向",
            InstallationStatus.Failed => "安装失败",
            InstallationStatus.Cancelled => "已取消",
            InstallationStatus.Interrupted => "监控中断",
            InstallationStatus.PassedThrough => "安全放行",
            InstallationStatus.Assisted => "辅助完成",
            InstallationStatus.Unverified => "待验证",
            _ => status.ToString()
        } : value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is InstallationStatus status ? status switch
        {
            InstallationStatus.Redirected or InstallationStatus.Succeeded or InstallationStatus.Assisted => "SuccessBrush",
            InstallationStatus.Failed => "DangerBrush",
            InstallationStatus.Cancelled or InstallationStatus.Interrupted or InstallationStatus.Unverified => "WarningBrush",
            InstallationStatus.Planned or InstallationStatus.Launching or InstallationStatus.Running => "AccentBrush",
            _ => "TextSecondaryBrush"
        } : "TextSecondaryBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusToSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is InstallationStatus status ? status switch
        {
            InstallationStatus.Redirected or InstallationStatus.Succeeded or InstallationStatus.Assisted => "SuccessSoftBrush",
            InstallationStatus.Failed => "DangerSoftBrush",
            InstallationStatus.Cancelled or InstallationStatus.Interrupted or InstallationStatus.Unverified => "WarningSoftBrush",
            InstallationStatus.Planned or InstallationStatus.Launching or InstallationStatus.Running => "AccentSoftBrush",
            _ => "SurfaceMutedBrush"
        } : "SurfaceMutedBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InstallerTypeToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is InstallerType type ? type switch
        {
            InstallerType.WindowsMsi => "Windows MSI",
            InstallerType.Wix => "WiX MSI",
            InstallerType.InnoSetup => "Inno Setup",
            InstallerType.Nsis => "NSIS",
            InstallerType.InstallShield => "InstallShield",
            InstallerType.CustomExe => "自定义 EXE",
            InstallerType.Msix => "MSIX / AppX",
            InstallerType.GameLauncher => "游戏启动器",
            _ => "未知安装器"
        } : "未知安装器";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ModeToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is HandlingMode mode ? mode switch
        {
            HandlingMode.Automatic => "自动重定向",
            HandlingMode.Assisted => "辅助模式",
            _ => "安全放行"
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CompatibilityToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CompatibilityLevel level ? level switch
        {
            CompatibilityLevel.Verified => "已验证",
            CompatibilityLevel.High => "高",
            CompatibilityLevel.Medium => "中",
            CompatibilityLevel.Experimental => "实验性",
            _ => "不支持"
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CompatibilityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is CompatibilityLevel level ? level switch
        {
            CompatibilityLevel.Verified or CompatibilityLevel.High => "SuccessBrush",
            CompatibilityLevel.Medium => "WarningBrush",
            CompatibilityLevel.Experimental => "InfoBrush",
            _ => "DangerBrush"
        } : "TextSecondaryBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TestStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TestStatus status ? status switch
        {
            TestStatus.Passed => "测试通过",
            TestStatus.Failed => "测试失败",
            TestStatus.Limited => "部分验证",
            _ => "待测试"
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RuleKindToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RuleKind kind ? kind switch
        {
            RuleKind.Mapping => "路径映射",
            RuleKind.AppWhitelist => "应用白名单",
            RuleKind.AppBlacklist => "应用黑名单",
            _ => "保护目录"
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dto)
            return dto.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        if (value is DateTime dt)
            return dt.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LongDateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dto)
            return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PercentTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{d:0.#}%" : "0%";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToOnOffConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "开启" : "关闭";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ShellStateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ShellIntegrationState state ? state switch
        {
            ShellIntegrationState.Enabled => "已接管",
            ShellIntegrationState.Partial => "部分接管",
            ShellIntegrationState.Unavailable => "不可用",
            _ => "未启用"
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

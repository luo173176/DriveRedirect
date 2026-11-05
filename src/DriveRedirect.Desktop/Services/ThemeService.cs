using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DriveRedirect.Desktop.Services;

public static class ThemeService
{
    private static readonly string LocalThemePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DriveRedirect",
        "ui-theme.txt");

    public static bool IsDark { get; private set; }
    public static string CurrentMode { get; private set; } = "System";

    public static void Apply(string mode)
    {
        var normalized = mode is "Light" or "Dark" ? mode : "System";
        var isDark = normalized == "Dark" || (normalized == "System" && !SystemUsesLightTheme());
        var source = isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        var app = Application.Current;
        if (app is null)
            return;

        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
            d.Source?.OriginalString.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        if (existing is not null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        CurrentMode = normalized;
        IsDark = isDark;
    }

    public static void ToggleLightDark()
    {
        var next = IsDark ? "Light" : "Dark";
        Apply(next);
        SaveLocalTheme(next);
    }

    public static void SaveLocalTheme(string mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalThemePath)!);
            File.WriteAllText(LocalThemePath, mode);
        }
        catch
        {
        }
    }

    public static string LoadLocalTheme()
    {
        try
        {
            if (File.Exists(LocalThemePath))
            {
                var text = File.ReadAllText(LocalThemePath).Trim();
                if (text is "Light" or "Dark" or "System")
                    return text;
            }
        }
        catch
        {
        }
        return "System";
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is not int intValue || intValue != 0;
        }
        catch
        {
            return true;
        }
    }
}

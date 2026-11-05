using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;

namespace DriveRedirect.Core.Installer;

public static class InstallerNames
{
    public static string InferSoftwareName(string? productName, string? fileDescription, string fileName)
    {
        var candidate = Clean(productName);
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Clean(fileDescription);
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Path.GetFileNameWithoutExtension(fileName);

        candidate = StripSetupSuffixes(candidate);
        return WindowsPathTools.SanitizeFolderName(candidate, Path.GetFileNameWithoutExtension(fileName));
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var text = value.Trim();
        if (text.Equals("Setup", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Installer", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Install", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("InstallShield Wizard", StringComparison.OrdinalIgnoreCase))
            return "";
        return text;
    }

    private static string StripSetupSuffixes(string value)
    {
        var result = value.Trim();
        string[] suffixes = [" Setup", " Installer", " Installation", " Install", " Setup Wizard", " Install Wizard", " Wizard"];
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in suffixes)
            {
                if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && result.Length > suffix.Length + 1)
                {
                    result = result[..^suffix.Length].Trim();
                    changed = true;
                }
            }
        }
        foreach (var suffix in new[] { "Setup", "Installer", "Installation" })
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                result.Length > suffix.Length + 1)
            {
                result = result[..^suffix.Length].Trim();
            }
        }
        return result;
    }

    public static bool IsMaintenanceInvocation(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return false;
        string[] tokens = ["/x", "/uninstall", "-uninstall", "--uninstall", "/f", "/repair", "-repair", "--repair", "/update", "-update", "--update", "/modify"];
        return tokens.Any(t => ContainsSwitch(arguments, t));
    }

    public static bool ContainsPathOverride(string arguments, InstallerType type)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return false;
        return type switch
        {
            InstallerType.InnoSetup => arguments.Contains("/DIR", StringComparison.OrdinalIgnoreCase),
            InstallerType.Nsis => arguments.Contains("/D=", StringComparison.OrdinalIgnoreCase),
            InstallerType.WindowsMsi or InstallerType.Wix => ContainsMsiPathProperty(arguments),
            InstallerType.InstallShield => arguments.Contains("INSTALLDIR", StringComparison.OrdinalIgnoreCase) ||
                                           arguments.Contains("INSTALLLOCATION", StringComparison.OrdinalIgnoreCase) ||
                                           arguments.Contains("/DIR", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool ContainsMsiPathProperty(string arguments)
    {
        string[] props = ["INSTALLDIR=", "INSTALLLOCATION=", "APPLICATIONFOLDER=", "INSTALLFOLDER=", "TARGETDIR=", "APPDIR="];
        return props.Any(p => arguments.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsSwitch(string arguments, string token)
    {
        var index = arguments.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false;
        if (index + token.Length == arguments.Length)
            return true;
        var next = arguments[index + token.Length];
        return char.IsWhiteSpace(next) || next == '=' || next == ':';
    }
}

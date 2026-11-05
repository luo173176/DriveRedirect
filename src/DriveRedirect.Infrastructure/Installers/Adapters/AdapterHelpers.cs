using System.Diagnostics;
using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

internal static class AdapterHelpers
{
    public static string? GuessProgramFilesDefault(InstallerArtifact artifact, string softwareName)
    {
        try
        {
            var is64 = PeMachineReader.Is64Bit(artifact.FilePath);
            var root = Environment.GetFolderPath(is64
                ? Environment.SpecialFolder.ProgramFiles
                : Environment.SpecialFolder.ProgramFilesX86);
            var safeName = WindowsPathTools.SanitizeFolderName(
                Core.Installer.InstallerNames.InferSoftwareName(artifact.ProductName, artifact.FileDescription, artifact.FileName));
            return Path.Combine(root, safeName);
        }
        catch
        {
            return null;
        }
    }

    public static string BuildInnoArguments(string originalArguments, string targetPath)
    {
        var prefix = originalArguments.Trim();
        var arguments = string.IsNullOrWhiteSpace(prefix)
            ? $"/DIR=\"{targetPath}\""
            : $"{prefix} /DIR=\"{targetPath}\"";
        return arguments;
    }

    public static string BuildNsisArguments(string originalArguments, string targetPath)
    {
        var prefix = originalArguments.Trim();
        var directory = $"/D={targetPath}";
        return string.IsNullOrWhiteSpace(prefix) ? directory : $"{prefix} {directory}";
    }

    public static string? FindExistingInstallLocation(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        using var localMachine = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.LocalMachine,
            Microsoft.Win32.RegistryView.Registry64);
        using var currentUser = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Registry64);
        string[] basePaths =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (var root in new[] { localMachine, currentUser })
        {
            foreach (var basePath in basePaths)
            {
                using var uninstall = root.OpenSubKey(basePath);
                if (uninstall is null)
                    continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    var display = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display) ||
                        !display.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var location = key?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                        return location;
                }
            }
        }
        return null;
    }

    public static string GetPeDescription(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static class PeMachineReader
    {
        public static bool Is64Bit(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new BinaryReader(stream);
                if (stream.Length < 0x40)
                    return Environment.Is64BitOperatingSystem;
                stream.Seek(0x3C, SeekOrigin.Begin);
                var peOffset = reader.ReadInt32();
                if (peOffset <= 0 || peOffset + 6 >= stream.Length)
                    return Environment.Is64BitOperatingSystem;
                stream.Seek(peOffset, SeekOrigin.Begin);
                if (reader.ReadUInt32() != 0x00004550)
                    return Environment.Is64BitOperatingSystem;
                var machine = reader.ReadUInt16();
                return machine is 0x8664 or 0xAA64;
            }
            catch
            {
                return Environment.Is64BitOperatingSystem;
            }
        }
    }
}

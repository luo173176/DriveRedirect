using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DriveRedirect.Core.Models;
using Microsoft.Win32;

namespace DriveRedirect.Infrastructure.Installers.Detection;

public sealed class MsiPackageReadException : Exception
{
    public MsiPackageReadException(string message, uint errorCode, Exception? inner = null)
        : base($"{message}（MSI 错误 {errorCode}）", inner)
    {
        ErrorCode = errorCode;
    }

    public uint ErrorCode { get; }
}

public sealed partial class MsiNativeReader
{
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;
    private const int SummaryPropertyTemplate = 7;

    private static readonly HashSet<string> PreferredDirectoryProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSTALLDIR", "INSTALLLOCATION", "INSTALLFOLDER", "APPLICATIONFOLDER", "APPDIR",
        "MAINAPPDIR", "INSTALLPATH", "APPLICATIONDIR", "PRODUCTINSTALLDIR"
    };

    public MsiPackageInfo Read(string msiPath)
    {
        var fullPath = Path.GetFullPath(msiPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("MSI 文件不存在。", fullPath);

        var openResult = MsiNative.MsiOpenDatabase(fullPath, IntPtr.Zero, out var database);
        if (openResult != 0)
            throw new MsiPackageReadException("无法以只读方式打开 MSI 数据库", openResult);

        try
        {
            var properties = ReadProperties(database);
            var directories = ReadDirectories(database);
            var componentCounts = ReadComponentCounts(database);
            var template = ReadTemplate(database);
            var is64Bit = template.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                          template.Contains("Intel64", StringComparison.OrdinalIgnoreCase) ||
                          template.Contains("AMD64", StringComparison.OrdinalIgnoreCase);

            var baseFolders = CreateBaseFolders(is64Bit);
            var resolved = ResolveDirectories(directories, properties, baseFolders);
            var selected = SelectInstallDirectory(resolved, componentCounts, properties);

            var productCode = GetProperty(properties, "ProductCode");
            var productName = GetProperty(properties, "ProductName");
            var allUsers = GetProperty(properties, "ALLUSERS");
            var perUser = GetProperty(properties, "MSIINSTALLPERUSER");
            var isPerMachine = string.Equals(allUsers, "1", StringComparison.OrdinalIgnoreCase) ||
                               !string.Equals(perUser, "1", StringComparison.OrdinalIgnoreCase);

            var defaultPath = selected?.Path;
            var hasExplicit = selected is not null &&
                              IsPublicPropertyName(selected.DirectoryId);
            var installChain = BuildInstallChain(selected, directories, resolved);
            var baseNode = installChain.LastOrDefault(n => n.IsBaseFolder);
            var anchorNode = baseNode is null
                ? null
                : installChain.SkipWhile(n => !ReferenceEquals(n, baseNode)).Skip(1).FirstOrDefault();

            var publicDirectoryProps = resolved.Values
                .Where(v => IsPublicPropertyName(v.DirectoryId))
                .Where(v => v.Path is not null)
                .Where(v => !IsBaseFolder(v.DirectoryId))
                .Where(v => componentCounts.GetValueOrDefault(v.DirectoryId) > 0)
                .Select(v => v.DirectoryId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => componentCounts.GetValueOrDefault(v))
                .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new MsiPackageInfo
            {
                ProductName = productName,
                Manufacturer = GetProperty(properties, "Manufacturer"),
                ProductVersion = GetProperty(properties, "ProductVersion"),
                ProductCode = productCode,
                UpgradeCode = GetProperty(properties, "UpgradeCode"),
                IsPerMachine = isPerMachine,
                IsInstalled = !string.IsNullOrWhiteSpace(productCode) && IsProductInstalled(productCode),
                DirectoryProperty = selected?.DirectoryId,
                DefaultInstallPath = defaultPath,
                ResolvedDirectoryPath = defaultPath,
                AnchorDirectoryId = anchorNode?.DirectoryId,
                AnchorDirectoryPath = anchorNode?.Path,
                BaseDirectoryId = baseNode?.DirectoryId,
                InstallDirectoryChain = installChain,
                HasExplicitInstallProperty = hasExplicit,
                PublicDirectoryProperties = publicDirectoryProps,
                AllProperties = properties
            };
        }
        finally
        {
            MsiNative.MsiCloseHandle(database);
        }
    }

    private static Dictionary<string, string> ReadProperties(IntPtr database)
    {
        var rows = Query(database, "SELECT `Property`, `Value` FROM `Property`", 2);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Count < 2 || row[0] is null)
                continue;
                    result[row[0]!] = row[1] ?? "";
        }
        return result;
    }

    private static List<DirectoryRow> ReadDirectories(IntPtr database)
    {
        var rows = Query(database, "SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`", 3);
        return rows.Select(row => new DirectoryRow(
            row[0] ?? "",
            row[1],
            row[2] ?? "")).Where(r => r.DirectoryId.Length > 0).ToList();
    }

    private static Dictionary<string, int> ReadComponentCounts(IntPtr database)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = Query(database, "SELECT `Directory_` FROM `Component`", 1);
            foreach (var row in rows)
            {
                var directory = row.Count > 0 ? row[0] : null;
                if (string.IsNullOrWhiteSpace(directory))
                    continue;
                counts[directory] = counts.GetValueOrDefault(directory) + 1;
            }
        }
        catch
        {
            // Component table is optional for metadata analysis.
        }
        return counts;
    }

    private static string ReadTemplate(IntPtr database)
    {
        IntPtr summary = IntPtr.Zero;
        try
        {
            var result = MsiNative.MsiGetSummaryInformation(database, null, 0, out summary);
            if (result != 0 || summary == IntPtr.Zero)
                return "";

            var buffer = new StringBuilder(256);
            uint length = (uint)buffer.Capacity;
            result = MsiNative.MsiSummaryInfoGetProperty(
                summary,
                SummaryPropertyTemplate,
                out _,
                out _,
                out _,
                buffer,
                ref length);
            return result == 0 ? buffer.ToString() : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            if (summary != IntPtr.Zero)
                MsiNative.MsiCloseHandle(summary);
        }
    }

    private static Dictionary<string, string> CreateBaseFolders(bool is64Bit)
    {
        var programFiles = Environment.GetFolderPath(is64Bit
            ? Environment.SpecialFolder.ProgramFiles
            : Environment.SpecialFolder.ProgramFilesX86);
        var commonFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var commonFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TARGETDIR"] = Path.GetPathRoot(programFiles) ?? @"C:\",
            ["ProgramFilesFolder"] = is64Bit ? programFiles : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramFiles64Folder"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles6432Folder"] = programFiles,
            ["CommonFilesFolder"] = is64Bit ? commonFiles : commonFilesX86,
            ["CommonFiles64Folder"] = commonFiles,
            ["LocalAppDataFolder"] = localAppData,
            ["CommonAppDataFolder"] = commonAppData,
            ["WindowsFolder"] = windows,
            ["SystemFolder"] = system,
            ["System64Folder"] = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            ["DesktopFolder"] = desktop,
            ["ProgramMenuFolder"] = programs,
            ["StartMenuFolder"] = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            ["FontsFolder"] = Path.Combine(windows, "Fonts"),
            ["WindowsVolume"] = Path.GetPathRoot(windows) ?? @"C:\",
            ["SourceDir"] = ""
        };
    }

    private static Dictionary<string, ResolvedDirectory> ResolveDirectories(
        IReadOnlyList<DirectoryRow> rows,
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> baseFolders)
    {
        var byId = rows
            .GroupBy(r => r.DirectoryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, ResolvedDirectory>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
            Resolve(row.DirectoryId, byId, properties, baseFolders, resolved, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        return resolved;
    }

    private static ResolvedDirectory Resolve(
        string directoryId,
        IReadOnlyDictionary<string, DirectoryRow> rows,
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> baseFolders,
        Dictionary<string, ResolvedDirectory> resolved,
        HashSet<string> visiting)
    {
        if (resolved.TryGetValue(directoryId, out var cached))
            return cached;

        if (baseFolders.TryGetValue(directoryId, out var basePath))
        {
            var value = new ResolvedDirectory(directoryId, basePath, 0);
            resolved[directoryId] = value;
            return value;
        }

        if (!rows.TryGetValue(directoryId, out var row))
        {
            var fallback = properties.TryGetValue(directoryId, out var propertyPath)
                ? ExpandPath(propertyPath, properties, baseFolders)
                : null;
            var unknown = new ResolvedDirectory(directoryId, fallback, 0);
            resolved[directoryId] = unknown;
            return unknown;
        }

        if (!visiting.Add(directoryId))
        {
            var cyclic = new ResolvedDirectory(directoryId, null, 0);
            resolved[directoryId] = cyclic;
            return cyclic;
        }

        string? parentPath = null;
        var depth = 1;
        if (!string.IsNullOrWhiteSpace(row.Parent))
        {
            var parent = Resolve(row.Parent, rows, properties, baseFolders, resolved, visiting);
            parentPath = parent.Path;
            depth = parent.Depth + 1;
        }

        var expandedDefault = ExpandPath(ExtractLongName(row.DefaultDir), properties, baseFolders);
        string? path;
        if (string.IsNullOrWhiteSpace(expandedDefault) || expandedDefault == ".")
        {
            path = parentPath;
        }
        else if (Path.IsPathFullyQualified(expandedDefault))
        {
            path = SafeNormalize(expandedDefault);
        }
        else if (!string.IsNullOrWhiteSpace(parentPath))
        {
            path = SafeNormalize(Path.Combine(parentPath, expandedDefault));
        }
        else
        {
            path = null;
        }

        visiting.Remove(directoryId);
        var result = new ResolvedDirectory(directoryId, path, depth);
        resolved[directoryId] = result;
        return result;
    }

    private static ResolvedDirectory? SelectInstallDirectory(
        IReadOnlyDictionary<string, ResolvedDirectory> resolved,
        IReadOnlyDictionary<string, int> componentCounts,
        IReadOnlyDictionary<string, string> properties)
    {
        var candidates = resolved.Values
            .Where(v => !string.IsNullOrWhiteSpace(v.Path))
            .Where(v => !IsBaseFolder(v.DirectoryId))
            .Where(v => IsInstallRootCandidate(v.Path!))
            .ToList();

        if (candidates.Count == 0)
            return null;

        var preferred = candidates
            .Where(c => PreferredDirectoryProperties.Contains(c.DirectoryId))
            .OrderByDescending(c => PreferredDirectoryProperties.Contains(c.DirectoryId))
            .ThenByDescending(c => componentCounts.GetValueOrDefault(c.DirectoryId))
            .ThenByDescending(c => c.Depth)
            .FirstOrDefault();
        if (preferred is not null)
            return preferred;

        var productName = GetProperty(properties, "ProductName");
        var manufacturer = GetProperty(properties, "Manufacturer");
        return candidates
            .OrderByDescending(c => componentCounts.GetValueOrDefault(c.DirectoryId) * 100 +
                                    (IsPublicPropertyName(c.DirectoryId) ? 20 : 0) +
                                    c.Depth +
                                    (!string.IsNullOrWhiteSpace(productName) &&
                                     c.Path!.Contains(productName, StringComparison.OrdinalIgnoreCase) ? 25 : 0) +
                                    (!string.IsNullOrWhiteSpace(manufacturer) &&
                                     c.Path!.Contains(manufacturer, StringComparison.OrdinalIgnoreCase) ? 10 : 0))
            .ThenByDescending(c => c.Path!.Length)
            .FirstOrDefault();
    }

    private static IReadOnlyList<MsiDirectoryNode> BuildInstallChain(
        ResolvedDirectory? selected,
        IReadOnlyList<DirectoryRow> directories,
        IReadOnlyDictionary<string, ResolvedDirectory> resolved)
    {
        if (selected is null)
            return [];

        var byId = directories
            .GroupBy(r => r.DirectoryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var chain = new List<MsiDirectoryNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = selected.DirectoryId;
        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
        {
            var isBase = IsBaseFolder(current);
            byId.TryGetValue(current, out var row);
            resolved.TryGetValue(current, out var node);
            chain.Add(new MsiDirectoryNode
            {
                DirectoryId = current,
                ParentId = row?.Parent,
                DefaultDir = row?.DefaultDir,
                Path = node?.Path,
                IsBaseFolder = isBase
            });
            if (isBase)
                break;
            if (row is null || string.IsNullOrWhiteSpace(row.Parent))
                break;
            current = row.Parent;
        }
        chain.Reverse();
        return chain;
    }

    private static bool IsInstallRootCandidate(string path)
    {
        if (path.Length == 0)
            return false;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Core.Rules.WindowsPathTools.IsSamePath(path, programFiles) ||
            Core.Rules.WindowsPathTools.IsSamePath(path, programFilesX86) ||
            Core.Rules.WindowsPathTools.IsSamePath(path, commonAppData) ||
            Core.Rules.WindowsPathTools.IsSamePath(path, localAppData))
            return false;

        return Core.Rules.WindowsPathTools.IsUnder(path, programFiles) ||
               Core.Rules.WindowsPathTools.IsUnder(path, programFilesX86) ||
               Core.Rules.WindowsPathTools.IsUnder(path, commonAppData) ||
               Core.Rules.WindowsPathTools.IsUnder(path, localAppData);
    }

    private static string? ExpandPath(
        string? raw,
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> baseFolders)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;
        var expanded = FolderPropertyRegex().Replace(raw, match =>
        {
            var name = match.Groups[1].Value;
            if (baseFolders.TryGetValue(name, out var folder))
                return folder;
            if (properties.TryGetValue(name, out var propertyValue))
                return propertyValue;
            return match.Value;
        });
        return expanded;
    }

    private static string ExtractLongName(string defaultDir)
    {
        if (string.IsNullOrWhiteSpace(defaultDir))
            return "";
        var value = defaultDir.Trim();
        var pipe = value.LastIndexOf('|');
        if (pipe >= 0)
            value = value[(pipe + 1)..];
        return value.Trim();
    }

    private static string? SafeNormalize(string path)
    {
        try
        {
            return Core.Rules.WindowsPathTools.NormalizeDirectory(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPublicPropertyName(string name)
        => name.Length > 0 && name.All(c => !char.IsLetter(c) || char.IsUpper(c));

    private static bool IsBaseFolder(string id) => id is
        "TARGETDIR" or "ProgramFilesFolder" or "ProgramFiles64Folder" or "ProgramFiles6432Folder" or
        "CommonFilesFolder" or "CommonFiles64Folder" or "LocalAppDataFolder" or "CommonAppDataFolder" or
        "WindowsFolder" or "SystemFolder" or "System64Folder" or "DesktopFolder" or "ProgramMenuFolder" or
        "StartMenuFolder" or "FontsFolder" or "WindowsVolume" or "SourceDir";

    private static string? GetProperty(IReadOnlyDictionary<string, string> properties, string name)
        => properties.TryGetValue(name, out var value) ? value : null;

    private static bool IsProductInstalled(string productCode)
    {
        var code = productCode.Trim();
        if (!code.StartsWith('{'))
            code = "{" + code + "}";
        string[] subKeys =
        [
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{code}",
            $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{code}"
        ];
        RegistryKey[] roots = [Registry.LocalMachine, Registry.CurrentUser];
        foreach (var root in roots)
        {
            foreach (var subKey in subKeys)
            {
                using var key = root.OpenSubKey(subKey);
                if (key is not null)
                    return true;
            }
        }
        return false;
    }

    private static List<List<string?>> Query(IntPtr database, string sql, int fieldCount)
    {
        IntPtr view = IntPtr.Zero;
        var result = MsiNative.MsiDatabaseOpenView(database, sql, out view);
        if (result != 0)
            throw new MsiPackageReadException("无法打开 MSI 查询", result);

        try
        {
            result = MsiNative.MsiViewExecute(view, IntPtr.Zero);
            if (result != 0)
                throw new MsiPackageReadException("无法执行 MSI 查询", result);

            var rows = new List<List<string?>>();
            while (true)
            {
                var fetch = MsiNative.MsiViewFetch(view, out var record);
                if (fetch == ErrorNoMoreItems)
                    break;
                if (fetch != 0)
                    throw new MsiPackageReadException("读取 MSI 记录失败", fetch);

                var row = new List<string?>(fieldCount);
                try
                {
                    for (var i = 1; i <= fieldCount; i++)
                        row.Add(ReadRecordString(record, (uint)i));
                }
                finally
                {
                    MsiNative.MsiCloseHandle(record);
                }
                rows.Add(row);
            }
            return rows;
        }
        finally
        {
            MsiNative.MsiCloseHandle(view);
        }
    }

    private static string? ReadRecordString(IntPtr record, uint field)
    {
        var size = 512u;
        var buffer = new StringBuilder((int)size);
        var result = MsiNative.MsiRecordGetString(record, field, buffer, ref size);
        if (result == ErrorMoreData)
        {
            size += 2;
            buffer = new StringBuilder((int)size);
            result = MsiNative.MsiRecordGetString(record, field, buffer, ref size);
        }
        if (result != 0)
            return null;
        var value = buffer.ToString();
        return value.Length == 0 ? null : value;
    }

    [GeneratedRegex(@"\[([A-Za-z0-9_]+)\]")]
    private static partial Regex FolderPropertyRegex();

    private sealed record DirectoryRow(string DirectoryId, string? Parent, string DefaultDir);
    private sealed record ResolvedDirectory(string DirectoryId, string? Path, int Depth);

    private static class MsiNative
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr database);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiViewExecute(IntPtr view, IntPtr record);

        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder buffer, ref uint bufferLength);

        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiCloseHandle(IntPtr handle);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiGetSummaryInformation(IntPtr database, string? databasePath, uint updateCount, out IntPtr summaryInfo);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiSummaryInfoGetProperty(
            IntPtr summaryInfo,
            int property,
            out uint dataType,
            out int intValue,
            out FileTime fileTime,
            StringBuilder valueBuffer,
            ref uint valueBufferLength);

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            public uint Low;
            public uint High;
        }
    }
}

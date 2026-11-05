using System.Text.RegularExpressions;

namespace DriveRedirect.Core.Rules;

public static partial class WindowsPathTools
{
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly string[] SystemProtectedRoots =
    [
        @"C:\Windows",
        @"C:\Program Files\WindowsApps",
        @"C:\Program Files\Common Files\microsoft shared",
        @"C:\Program Files\Windows Defender",
        @"C:\Program Files\Windows NT",
        @"C:\Program Files (x86)\Windows Defender",
        @"C:\ProgramData\Microsoft\Windows",
        @"C:\ProgramData\Microsoft\Windows Defender",
        @"C:\ProgramData\Package Cache"
    ];

    public static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径为空。", nameof(path));

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.StartsWith(@"\\?\", StringComparison.Ordinal))
            expanded = expanded[4..];

        if (expanded.StartsWith(@"\??\", StringComparison.Ordinal))
            throw new InvalidOperationException("不支持设备路径。");

        if (!Path.IsPathFullyQualified(expanded))
            throw new InvalidOperationException("必须使用绝对路径。");

        if (expanded.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("不支持 UNC 网络路径。");

        if (expanded.Contains(":$", StringComparison.Ordinal) ||
            expanded.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            throw new InvalidOperationException("路径包含不受支持的字符。");

        var full = Path.GetFullPath(expanded);
        var root = Path.GetPathRoot(full) ?? "";
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return root;

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string NormalizeDriveRoot(string drive)
    {
        if (string.IsNullOrWhiteSpace(drive))
            throw new ArgumentException("磁盘为空。", nameof(drive));
        var value = drive.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!value.EndsWith(':'))
            value += ":";
        if (!Regex.IsMatch(value, "^[A-Za-z]:$"))
            throw new ArgumentException("磁盘格式应为 D:。", nameof(drive));
        return value.ToUpperInvariant() + Path.DirectorySeparatorChar;
    }

    public static bool IsUnder(string path, string root)
    {
        var p = NormalizeDirectory(path);
        var r = NormalizeDirectory(root);
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderAny(string path, IEnumerable<string> roots)
        => roots.Any(root => IsUnder(path, root));

    public static string GetRelativeTo(string path, string root)
    {
        var p = NormalizeDirectory(path);
        var r = NormalizeDirectory(root);
        if (!IsUnder(p, r))
            throw new InvalidOperationException("路径不在指定根目录内。");
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
            return "";
        return p[(r.Length + 1)..];
    }

    public static string CombineUnder(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidOperationException("相对路径不能是绝对路径。");
        var normalized = NormalizeDirectory(Path.Combine(root, relative));
        if (!IsUnder(normalized, root))
            throw new InvalidOperationException("检测到目录穿越。");
        return normalized;
    }

    public static string SanitizeFolderName(string name, string fallback = "Application")
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        foreach (var ch in InvalidNameChars)
            candidate = candidate.Replace(ch, '_');
        candidate = WhiteSpaceRegex().Replace(candidate, " ").Trim(' ', '.');
        if (candidate.Length == 0)
            candidate = fallback;
        if (candidate.Length > 100)
            candidate = candidate[..100].Trim();
        return candidate;
    }

    public static bool IsSamePath(string? left, string? right)
    {
        if (left is null || right is null)
            return false;
        try
        {
            return string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool ContainsTraversal(string path)
    {
        var segments = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s == ".." || s == ".");
    }

    public static bool IsSystemProtectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var normalized = NormalizeDirectory(path);
            return SystemProtectedRoots.Any(root => IsUnder(normalized, root));
        }
        catch
        {
            return true;
        }
    }

    public static IReadOnlyList<string> GetSystemProtectedRoots() => SystemProtectedRoots;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRegex();
}

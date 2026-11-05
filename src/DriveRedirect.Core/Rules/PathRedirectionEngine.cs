using DriveRedirect.Core.Models;

namespace DriveRedirect.Core.Rules;

public sealed class PathRedirectionEngine
{
    private static readonly IReadOnlyList<string> ProtectedSystemRoots = WindowsPathTools.GetSystemProtectedRoots();

    private readonly IDiskProbe _diskProbe;

    public PathRedirectionEngine(IDiskProbe diskProbe)
    {
        _diskProbe = diskProbe;
    }

    public PathEvaluationResult Evaluate(
        string softwareName,
        string originalPath,
        IReadOnlyList<PathRule> rules,
        AppSettings settings,
        bool userSpecifiedPath = false,
        long installerSizeBytes = 0)
    {
        string normalizedOriginal;
        try
        {
            normalizedOriginal = WindowsPathTools.NormalizeDirectory(originalPath);
        }
        catch (Exception ex)
        {
            return Deny(originalPath, "原始路径无效：" + ex.Message);
        }

        if (userSpecifiedPath)
            return Deny(normalizedOriginal, "用户在安装器中指定了路径，尊重用户选择。");

        if (IsProtectedPath(normalizedOriginal, rules))
            return Deny(normalizedOriginal, "目标属于 Windows 系统保护目录，不允许重定向。");

        var blacklist = rules.FirstOrDefault(r =>
            r.Kind == RuleKind.AppBlacklist && r.Enabled &&
            (MatchesAppName(softwareName, r.SourcePattern) ||
             IsPathPatternMatch(normalizedOriginal, r.SourcePattern)));
        if (blacklist is not null)
            return Deny(normalizedOriginal, $"命中黑名单规则「{blacklist.Name}」。");

        var sourceRoot = Path.GetPathRoot(normalizedOriginal);
        if (!string.Equals(sourceRoot, @"C:\", StringComparison.OrdinalIgnoreCase))
            return Deny(normalizedOriginal, "原始安装路径不在 C 盘，无需重定向。");

        var isAppData = IsAppDataPath(normalizedOriginal);
        var whitelisted = rules.FirstOrDefault(r =>
            r.Kind == RuleKind.AppWhitelist && r.Enabled && r.AllowAppData &&
            MatchesAppName(softwareName, r.SourcePattern));

        if (isAppData && !settings.AllowAppDataRedirection)
            return Deny(normalizedOriginal, "AppData 应用级重定向总开关未启用，禁止迁移用户数据目录。");

        if (isAppData && whitelisted is null)
            return Deny(normalizedOriginal, "AppData 未获应用级白名单授权，禁止全局迁移。");

        var mappingCandidates = rules
            .Where(r => r.Kind == RuleKind.Mapping && r.Enabled && !string.IsNullOrWhiteSpace(r.TargetPattern))
            .Select(r => new
            {
                Rule = r,
                Attempt = TryNormalize(r.SourcePattern),
                Target = TryNormalize(r.TargetPattern!)
            })
            .Where(x => x.Attempt.Success && x.Target.Success && WindowsPathTools.IsUnder(normalizedOriginal, x.Attempt.Value!))
            .OrderByDescending(x => x.Attempt.Value!.Length)
            .ThenByDescending(x => x.Rule.Priority)
            .ToList();

        if (mappingCandidates.Count == 0)
            return Deny(normalizedOriginal, "没有匹配的 C 盘路径映射规则。");

        var mapping = mappingCandidates[0];
        if (isAppData && !mapping.Rule.AllowAppData)
            return Deny(normalizedOriginal, "该 AppData 映射未启用应用级授权。");

        var targetDrive = WindowsPathTools.NormalizeDriveRoot(settings.TargetDrive);
        var configuredTargetRoot = mapping.Target.Value!;
        var configuredDrive = Path.GetPathRoot(configuredTargetRoot);
        if (!string.Equals(configuredDrive, targetDrive, StringComparison.OrdinalIgnoreCase))
            return Deny(normalizedOriginal, $"映射目标不在固定目标磁盘 {targetDrive.TrimEnd('\\')} 上。");

        string relative;
        try
        {
            relative = WindowsPathTools.GetRelativeTo(normalizedOriginal, mapping.Attempt.Value!);
        }
        catch (Exception ex)
        {
            return Deny(normalizedOriginal, "无法计算相对路径：" + ex.Message);
        }

        string targetPath;
        try
        {
            targetPath = relative.Length == 0
                ? WindowsPathTools.NormalizeDirectory(configuredTargetRoot)
                : WindowsPathTools.CombineUnder(configuredTargetRoot, relative);
        }
        catch (Exception ex)
        {
            return Deny(normalizedOriginal, "目标路径校验失败：" + ex.Message);
        }

        if (Path.GetPathRoot(targetPath)?.StartsWith(targetDrive, StringComparison.OrdinalIgnoreCase) != true)
            return Deny(normalizedOriginal, "目标路径未落在固定目标磁盘。");

        var disk = _diskProbe.Probe(targetDrive);
        if (!disk.Exists || !disk.IsReady)
            return Deny(normalizedOriginal, $"{targetDrive.TrimEnd('\\')} 盘不存在或未就绪：{disk.Error}");
        if (!disk.IsWritable)
            return Deny(normalizedOriginal, $"{targetDrive.TrimEnd('\\')} 盘不可写：{disk.Error}");
        if (!disk.IsFixed)
            return Deny(normalizedOriginal, $"{targetDrive.TrimEnd('\\')} 盘不是本地固定磁盘。");

        var minimumFree = Math.Max(settings.MinimumFreeSpaceGb, 1) * 1024L * 1024L * 1024L;
        var requiredFree = Math.Max(minimumFree, installerSizeBytes > 0 ? installerSizeBytes : 0);
        if (disk.FreeBytes < requiredFree)
            return Deny(normalizedOriginal, $"{targetDrive.TrimEnd('\\')} 盘剩余空间不足，需要至少 {FormatBytes(requiredFree)}。");

        if (IsProtectedPath(targetPath, rules))
            return Deny(normalizedOriginal, "计算出的目标路径属于受保护目录。");

        if (Directory.Exists(normalizedOriginal) && Directory.EnumerateFileSystemEntries(normalizedOriginal).Any())
            return Deny(normalizedOriginal, "C 盘目标目录已存在且非空，为避免迁移已安装软件，安全放行。");

        return new PathEvaluationResult
        {
            Allowed = true,
            OriginalPath = normalizedOriginal,
            TargetPath = targetPath,
            MatchedRuleName = mapping.Rule.Name,
            Reason = $"按映射「{mapping.Rule.Name}」重定向到 {targetDrive.TrimEnd('\\')} 盘。"
        };
    }

    public bool IsProtectedPath(string path, IReadOnlyList<PathRule>? rules = null)
    {
        try
        {
            var normalized = WindowsPathTools.NormalizeDirectory(path);
            if (ProtectedSystemRoots.Any(root => WindowsPathTools.IsUnder(normalized, root)))
                return true;

            if (rules is not null)
            {
                foreach (var rule in rules.Where(r => r.Kind == RuleKind.ProtectedPath && r.Enabled))
                {
                    if (TryNormalize(rule.SourcePattern) is { Success: true } root &&
                        WindowsPathTools.IsUnder(normalized, root.Value!))
                        return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    public static IReadOnlyList<string> GetDefaultProtectedPaths() => ProtectedSystemRoots;

    public static IReadOnlyList<PathRule> CreateDefaultRules()
    {
        var now = DateTimeOffset.Now;
        return
        [
            new PathRule
            {
                Name = "Program Files",
                Kind = RuleKind.Mapping,
                SourcePattern = @"C:\Program Files",
                TargetPattern = @"D:\Program Files",
                Enabled = true,
                Priority = 100,
                IsBuiltIn = true,
                CreatedAt = now,
                Note = "64 位程序默认安装目录"
            },
            new PathRule
            {
                Name = "Program Files (x86)",
                Kind = RuleKind.Mapping,
                SourcePattern = @"C:\Program Files (x86)",
                TargetPattern = @"D:\Program Files (x86)",
                Enabled = true,
                Priority = 90,
                IsBuiltIn = true,
                CreatedAt = now,
                Note = "32 位程序默认安装目录"
            },
            new PathRule
            {
                Name = "ProgramData",
                Kind = RuleKind.Mapping,
                SourcePattern = @"C:\ProgramData",
                TargetPattern = @"D:\ProgramData",
                Enabled = true,
                Priority = 80,
                IsBuiltIn = true,
                CreatedAt = now,
                Note = "共享程序数据；仅安装器明确支持时生效"
            },
            new PathRule
            {
                Name = "用户 AppData Local",
                Kind = RuleKind.Mapping,
                SourcePattern = @"C:\Users\%USERNAME%\AppData\Local",
                TargetPattern = @"D:\Users\%USERNAME%\AppData\Local",
                Enabled = false,
                AllowAppData = false,
                Priority = 50,
                IsBuiltIn = true,
                CreatedAt = now,
                Note = "默认禁用；必须配置应用级白名单"
            },
            new PathRule
            {
                Name = "用户 AppData Roaming",
                Kind = RuleKind.Mapping,
                SourcePattern = @"C:\Users\%USERNAME%\AppData\Roaming",
                TargetPattern = @"D:\Users\%USERNAME%\AppData\Roaming",
                Enabled = false,
                AllowAppData = false,
                Priority = 40,
                IsBuiltIn = true,
                CreatedAt = now,
                Note = "默认禁用；必须配置应用级白名单"
            }
        ];
    }

    public static bool IsAppDataPath(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains(@"\AppData\Local\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\AppData\Roaming\", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(@"\AppData\Local", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(@"\AppData\Roaming", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesAppName(string softwareName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;
        if (pattern.Contains('*') || pattern.Contains('?'))
            return WildcardMatch(softwareName, pattern);
        return softwareName.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
               pattern.Contains(softwareName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathPatternMatch(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || !Path.IsPathRooted(Environment.ExpandEnvironmentVariables(pattern)))
            return false;
        try
        {
            return WindowsPathTools.IsUnder(path, Environment.ExpandEnvironmentVariables(pattern));
        }
        catch
        {
            return false;
        }
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        int v = 0, p = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                v++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private static (bool Success, string? Value) TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, null);
        try
        {
            return (true, WindowsPathTools.NormalizeDirectory(path));
        }
        catch
        {
            return (false, null);
        }
    }

    private static PathEvaluationResult Deny(string original, string reason) => new()
    {
        Allowed = false,
        OriginalPath = original,
        Reason = reason
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.#} {units[index]}";
    }
}

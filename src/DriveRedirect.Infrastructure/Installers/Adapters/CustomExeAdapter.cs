using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class CustomExeAdapter : IInstallerAdapter
{
    private readonly InstallerProfileStore _profiles;

    public CustomExeAdapter(InstallerProfileStore profiles)
    {
        _profiles = profiles;
    }

    public string Id => "custom-exe";
    public string DisplayName => "自定义 EXE 安装器";
    public string Version => "1.0.0";
    public InstallerType InstallerType => InstallerType.CustomExe;

    public bool CanHandle(InstallerArtifact artifact) => artifact.Type == InstallerType.CustomExe;

    public async Task<AdapterAssessment> AssessAsync(InstallerArtifact artifact, InstallerContext context, CancellationToken ct = default)
    {
        var softwareName = InstallerNames.InferSoftwareName(artifact.ProductName, artifact.FileDescription, artifact.FileName);
        var profile = await _profiles.FindMatchAsync(artifact, ct);
        var maintenance = InstallerNames.IsMaintenanceInvocation(context.OriginalArguments);

        if (profile is null)
        {
            return new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = AdapterHelpers.GuessProgramFilesDefault(artifact, softwareName),
                SupportsAutomaticRedirect = false,
                Compatibility = CompatibilityLevel.Medium,
                Strategy = "未知自定义 EXE，安全放行",
                Limitation = "没有经过验证的安装参数配置，不会向安装器注入任何未知参数。",
                AssistedGuidance = "请在安装界面中手动选择 D 盘目标目录。"
            };
        }

        var originalPath = !string.IsNullOrWhiteSpace(profile.OriginalPathTemplate)
            ? Expand(profile.OriginalPathTemplate, softwareName, artifact.CompanyName, targetPath: null)
            : AdapterHelpers.GuessProgramFilesDefault(artifact, softwareName);

        if (maintenance)
        {
            return new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = originalPath,
                SupportsAutomaticRedirect = false,
                Compatibility = profile.Compatibility,
                Strategy = "自定义配置维护调用安全放行",
                Limitation = "检测到更新、修复或卸载参数。"
            };
        }

        if (InstallerNames.ContainsPathOverride(context.OriginalArguments, InstallerType.CustomExe))
        {
            return new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = originalPath,
                SupportsAutomaticRedirect = false,
                Compatibility = profile.Compatibility,
                Strategy = "命令行已包含路径参数",
                UserSpecifiedPathReason = "尊重用户传入的安装路径。",
                Limitation = profile.Notes
            };
        }

        return new AdapterAssessment
        {
            AdapterId = Id,
            AdapterVersion = Version,
            InstallerType = InstallerType,
            SoftwareName = softwareName,
            OriginalInstallPath = originalPath,
            SupportsAutomaticRedirect = profile.Verified && !string.IsNullOrWhiteSpace(profile.ArgumentTemplate),
            Compatibility = profile.Compatibility,
            Strategy = profile.Verified ? $"自定义配置「{profile.Name}」" : $"自定义配置「{profile.Name}」尚未验证",
            Limitation = profile.Notes ?? (profile.Verified ? null : "配置未通过验证，只提供辅助提示。"),
            AssistedGuidance = profile.Verified
                ? null
                : $"请在安装界面中把路径改为 D 盘目标目录。"
        };
    }

    public async Task<AdapterLaunchSpec> BuildLaunchAsync(
        InstallerArtifact artifact,
        AdapterAssessment assessment,
        string targetPath,
        InstallerContext context,
        CancellationToken ct = default)
    {
        var profile = await _profiles.FindMatchAsync(artifact, ct)
            ?? throw new InvalidOperationException("自定义安装器配置已失效。");
        if (!profile.Verified || string.IsNullOrWhiteSpace(profile.ArgumentTemplate))
            throw new InvalidOperationException("自定义安装器配置未验证。");

        var template = Expand(profile.ArgumentTemplate, assessment.SoftwareName, artifact.CompanyName, targetPath);
        var arguments = string.IsNullOrWhiteSpace(context.OriginalArguments)
            ? template
            : $"{context.OriginalArguments.Trim()} {template}";

        return new AdapterLaunchSpec
        {
            FileName = artifact.FilePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(artifact.FilePath) ?? Environment.CurrentDirectory,
            VerificationPath = targetPath,
            StrategyDetail = "经验证的自定义参数模板"
        };
    }

    private static string Expand(string template, string appName, string? company, string? targetPath)
        => template
            .Replace("{appName}", appName, StringComparison.OrdinalIgnoreCase)
            .Replace("{company}", company ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{target}", targetPath ?? "", StringComparison.OrdinalIgnoreCase);
}

using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class GameLauncherAdapter : IInstallerAdapter
{
    private readonly InstallerProfileStore _profiles;

    public GameLauncherAdapter(InstallerProfileStore profiles)
    {
        _profiles = profiles;
    }

    public string Id => "game-launcher";
    public string DisplayName => "游戏启动器";
    public string Version => "1.0.0";
    public InstallerType InstallerType => InstallerType.GameLauncher;

    public bool CanHandle(InstallerArtifact artifact) => artifact.Type == InstallerType.GameLauncher;

    public async Task<AdapterAssessment> AssessAsync(InstallerArtifact artifact, InstallerContext context, CancellationToken ct = default)
    {
        var softwareName = InstallerNames.InferSoftwareName(artifact.ProductName, artifact.FileDescription, artifact.FileName);
        var profile = await _profiles.FindMatchAsync(artifact, ct);
        var originalPath = profile?.OriginalPathTemplate is not null
            ? Expand(profile.OriginalPathTemplate, softwareName)
            : AdapterHelpers.GuessProgramFilesDefault(artifact, softwareName);

        if (profile is { Verified: true, ArgumentTemplate: not null })
        {
            return new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = originalPath,
                SupportsAutomaticRedirect = true,
                Compatibility = profile.Compatibility,
                Strategy = $"游戏启动器配置「{profile.Name}」",
                Limitation = "仅验证过的游戏启动器安装包使用自动路径参数；启动器后续下载的游戏库路径由其自身设置管理。"
            };
        }

        return new AdapterAssessment
        {
            AdapterId = Id,
            AdapterVersion = Version,
            InstallerType = InstallerType,
            SoftwareName = softwareName,
            OriginalInstallPath = originalPath,
            SupportsAutomaticRedirect = false,
            Compatibility = CompatibilityLevel.Medium,
            Strategy = "识别为游戏启动器，辅助放行",
            Limitation = "不同游戏启动器使用不同参数与下载库设置，禁止对所有启动器套用同一参数。",
            AssistedGuidance = "请在启动器安装界面选择 D 盘目录；安装后还需在启动器设置中单独更改游戏库路径。"
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
            ?? throw new InvalidOperationException("游戏启动器配置已失效。");
        if (!profile.Verified || string.IsNullOrWhiteSpace(profile.ArgumentTemplate))
            throw new InvalidOperationException("游戏启动器配置未验证。");
        var template = profile.ArgumentTemplate
            .Replace("{target}", targetPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{appName}", assessment.SoftwareName, StringComparison.OrdinalIgnoreCase);
        return new AdapterLaunchSpec
        {
            FileName = artifact.FilePath,
            Arguments = string.IsNullOrWhiteSpace(context.OriginalArguments)
                ? template
                : $"{context.OriginalArguments.Trim()} {template}",
            WorkingDirectory = Path.GetDirectoryName(artifact.FilePath) ?? Environment.CurrentDirectory,
            VerificationPath = targetPath,
            StrategyDetail = "经验证的游戏启动器参数模板"
        };
    }

    private static string Expand(string template, string appName)
        => template.Replace("{appName}", appName, StringComparison.OrdinalIgnoreCase);
}

using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class InnoSetupAdapter : IInstallerAdapter
{
    public string Id => "inno-setup";
    public string DisplayName => "Inno Setup";
    public string Version => "1.0.0";
    public InstallerType InstallerType => InstallerType.InnoSetup;

    public bool CanHandle(InstallerArtifact artifact) => artifact.Type == InstallerType.InnoSetup;

    public Task<AdapterAssessment> AssessAsync(InstallerArtifact artifact, InstallerContext context, CancellationToken ct = default)
    {
        var softwareName = InstallerNames.InferSoftwareName(artifact.ProductName, artifact.FileDescription, artifact.FileName);
        var originalPath = AdapterHelpers.GuessProgramFilesDefault(artifact, softwareName);
        var userOverride = InstallerNames.ContainsPathOverride(context.OriginalArguments, artifact.Type);
        var maintenance = InstallerNames.IsMaintenanceInvocation(context.OriginalArguments);

        var assessment = new AdapterAssessment
        {
            AdapterId = Id,
            AdapterVersion = Version,
            InstallerType = InstallerType,
            SoftwareName = softwareName,
            OriginalInstallPath = originalPath,
            SupportsAutomaticRedirect = !userOverride && !maintenance,
            Compatibility = CompatibilityLevel.High,
            Strategy = "Inno Setup /DIR 默认目录参数",
            UserSpecifiedPathReason = userOverride ? "命令行已包含 /DIR，尊重用户选择。" : null,
            Limitation = "默认目录由编译脚本决定；DriveRedirect 以应用名推断并直接设置 D 盘目录。更新与卸载继续使用安装器自身逻辑。",
            AssistedGuidance = originalPath is null ? "请在安装界面中手动选择 D 盘目标目录。" : null
        };

        if (maintenance)
        {
            return Task.FromResult(new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = originalPath,
                SupportsAutomaticRedirect = false,
                Compatibility = CompatibilityLevel.High,
                Strategy = "维护调用安全放行",
                Limitation = "检测到更新、修复或卸载参数。"
            });
        }

        return Task.FromResult(assessment);
    }

    public Task<AdapterLaunchSpec> BuildLaunchAsync(
        InstallerArtifact artifact,
        AdapterAssessment assessment,
        string targetPath,
        InstallerContext context,
        CancellationToken ct = default)
    {
        var args = AdapterHelpers.BuildInnoArguments(context.OriginalArguments, targetPath);
        return Task.FromResult(new AdapterLaunchSpec
        {
            FileName = artifact.FilePath,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(artifact.FilePath) ?? Environment.CurrentDirectory,
            VerificationPath = targetPath,
            StrategyDetail = "/DIR 默认目录重写"
        });
    }
}

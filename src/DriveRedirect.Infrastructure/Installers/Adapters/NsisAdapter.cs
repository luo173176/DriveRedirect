using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class NsisAdapter : IInstallerAdapter
{
    public string Id => "nsis";
    public string DisplayName => "NSIS";
    public string Version => "1.0.0";
    public InstallerType InstallerType => InstallerType.Nsis;

    public bool CanHandle(InstallerArtifact artifact) => artifact.Type == InstallerType.Nsis;

    public Task<AdapterAssessment> AssessAsync(InstallerArtifact artifact, InstallerContext context, CancellationToken ct = default)
    {
        var softwareName = InstallerNames.InferSoftwareName(artifact.ProductName, artifact.FileDescription, artifact.FileName);
        var originalPath = AdapterHelpers.GuessProgramFilesDefault(artifact, softwareName);
        var userOverride = InstallerNames.ContainsPathOverride(context.OriginalArguments, artifact.Type);
        var maintenance = InstallerNames.IsMaintenanceInvocation(context.OriginalArguments);

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

        return Task.FromResult(new AdapterAssessment
        {
            AdapterId = Id,
            AdapterVersion = Version,
            InstallerType = InstallerType,
            SoftwareName = softwareName,
            OriginalInstallPath = originalPath,
            SupportsAutomaticRedirect = !userOverride,
            Compatibility = CompatibilityLevel.High,
            Strategy = "NSIS /D 默认安装目录参数",
            UserSpecifiedPathReason = userOverride ? "命令行已包含 /D，尊重用户选择。" : null,
            Limitation = "仅有使用标准 $INSTDIR 页面且接受 /D 的 NSIS 安装器可可靠重定向；不使用该页面的自定义安装器会被安全放行。",
            AssistedGuidance = originalPath is null ? "请在安装界面中手动选择 D 盘目标目录。" : null
        });
    }

    public Task<AdapterLaunchSpec> BuildLaunchAsync(
        InstallerArtifact artifact,
        AdapterAssessment assessment,
        string targetPath,
        InstallerContext context,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AdapterLaunchSpec
        {
            FileName = artifact.FilePath,
            Arguments = AdapterHelpers.BuildNsisArguments(context.OriginalArguments, targetPath),
            WorkingDirectory = Path.GetDirectoryName(artifact.FilePath) ?? Environment.CurrentDirectory,
            VerificationPath = targetPath,
            StrategyDetail = "/D 默认安装目录重写"
        });
    }
}

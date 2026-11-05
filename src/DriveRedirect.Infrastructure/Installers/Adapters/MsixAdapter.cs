using DriveRedirect.Core.Models;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class MsixAdapter : IInstallerAdapter
{
    public string Id => "msix";
    public string DisplayName => "MSIX / AppX";
    public string Version => "1.0.0";
    public InstallerType InstallerType => InstallerType.Msix;

    public bool CanHandle(InstallerArtifact artifact) => artifact.Type == InstallerType.Msix;

    public Task<AdapterAssessment> AssessAsync(InstallerArtifact artifact, InstallerContext context, CancellationToken ct = default)
    {
        return Task.FromResult(new AdapterAssessment
        {
            AdapterId = Id,
            AdapterVersion = Version,
            InstallerType = InstallerType,
            SoftwareName = Core.Installer.InstallerNames.InferSoftwareName(
                artifact.ProductName, artifact.FileDescription, artifact.FileName),
            OriginalInstallPath = null,
            SupportsAutomaticRedirect = false,
            Compatibility = CompatibilityLevel.Unsupported,
            Strategy = "Windows 官方 MSIX 部署机制，安全放行",
            Limitation = "MSIX/AppX 的安装位置由 Windows 应用部署子系统管理，不支持第三方改写；DriveRedirect 不会破坏包完整性或签名。",
            AssistedGuidance = "可通过 Windows 设置将新应用的默认安装驱动器设为 D:；DriveRedirect 不强行修改系统管理的包位置。"
        });
    }

    public Task<AdapterLaunchSpec> BuildLaunchAsync(
        InstallerArtifact artifact,
        AdapterAssessment assessment,
        string targetPath,
        InstallerContext context,
        CancellationToken ct = default)
        => throw new NotSupportedException("MSIX 不支持路径重写。");
}

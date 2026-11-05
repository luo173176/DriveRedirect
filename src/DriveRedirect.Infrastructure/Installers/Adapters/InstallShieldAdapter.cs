using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class InstallShieldAdapter : IInstallerAdapter
{
    public string Id => "installshield";
    public string DisplayName => "InstallShield";
    public string Version => "1.0.0";
    public InstallerType InstallerType => InstallerType.InstallShield;

    public bool CanHandle(InstallerArtifact artifact) => artifact.Type == InstallerType.InstallShield;

    public Task<AdapterAssessment> AssessAsync(InstallerArtifact artifact, InstallerContext context, CancellationToken ct = default)
    {
        var softwareName = InstallerNames.InferSoftwareName(artifact.ProductName, artifact.FileDescription, artifact.FileName);
        var originalPath = AdapterHelpers.GuessProgramFilesDefault(artifact, softwareName);
        var userOverride = InstallerNames.ContainsPathOverride(context.OriginalArguments, artifact.Type);
        var maintenance = InstallerNames.IsMaintenanceInvocation(context.OriginalArguments);
        var basicMsi = artifact.DetectionSignals.Any(s =>
            s.Contains("Basic MSI", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("ISBasicMsi", StringComparison.OrdinalIgnoreCase));

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
                Compatibility = CompatibilityLevel.Medium,
                Strategy = "维护调用安全放行",
                Limitation = "检测到更新、修复或卸载参数。"
            });
        }

        if (userOverride)
        {
            return Task.FromResult(new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = originalPath,
                SupportsAutomaticRedirect = false,
                Compatibility = CompatibilityLevel.Medium,
                Strategy = "用户已指定 InstallShield 安装目录",
                UserSpecifiedPathReason = "命令行包含 INSTALLDIR、INSTALLLOCATION 或 /DIR。",
                Limitation = "尊重用户传入的 InstallShield 参数。"
            });
        }

        return Task.FromResult(new AdapterAssessment
        {
            AdapterId = Id,
            AdapterVersion = Version,
            InstallerType = InstallerType,
            SoftwareName = softwareName,
            OriginalInstallPath = originalPath,
            PublicDirectoryProperty = basicMsi ? "INSTALLDIR" : null,
            SupportsAutomaticRedirect = basicMsi,
            Compatibility = CompatibilityLevel.Medium,
            Strategy = basicMsi ? "InstallShield Basic MSI /v INSTALLDIR 传递" : "InstallShield 安装模式未确认，辅助放行",
            Limitation = basicMsi
                ? "仅对 Basic MSI 模式使用 /v 传递 INSTALLDIR；未在全部 InstallShield 版本上验证。"
                : "InstallScript 或混合模式不能可靠通过通用参数改写目录，禁止盲目注入参数。",
            AssistedGuidance = originalPath is null
                ? "请在安装界面中选择 D 盘目标目录。"
                : $"请在安装界面把安装位置改为 {originalPath.Replace(@"C:\", @"D:\", StringComparison.OrdinalIgnoreCase)}。"
        });
    }

    public Task<AdapterLaunchSpec> BuildLaunchAsync(
        InstallerArtifact artifact,
        AdapterAssessment assessment,
        string targetPath,
        InstallerContext context,
        CancellationToken ct = default)
    {
        var prefix = context.OriginalArguments.Trim();
        var passThrough = $"/v\"INSTALLDIR=\\\"{targetPath}\\\"\"";
        var arguments = string.IsNullOrWhiteSpace(prefix) ? passThrough : $"{prefix} {passThrough}";
        return Task.FromResult(new AdapterLaunchSpec
        {
            FileName = artifact.FilePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(artifact.FilePath) ?? Environment.CurrentDirectory,
            VerificationPath = targetPath,
            StrategyDetail = "/v 传递 MSI INSTALLDIR"
        });
    }
}

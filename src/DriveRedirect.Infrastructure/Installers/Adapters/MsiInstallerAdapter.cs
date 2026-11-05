using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Installers.Detection;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public class MsiInstallerAdapter : IInstallerAdapter
{
    private readonly MsiTransformBuilder _transformBuilder;
    private readonly bool _isWix;

    public MsiInstallerAdapter(MsiTransformBuilder transformBuilder, bool isWix = false)
    {
        _transformBuilder = transformBuilder;
        _isWix = isWix;
    }

    public string Id => _isWix ? "wix-msi" : "msi";
    public string DisplayName => _isWix ? "WiX MSI" : "Windows Installer (MSI)";
    public string Version => "1.0.0";
    public InstallerType InstallerType => _isWix ? InstallerType.Wix : InstallerType.WindowsMsi;

    public bool CanHandle(InstallerArtifact artifact)
        => _isWix ? artifact.Type == InstallerType.Wix : artifact.Type == InstallerType.WindowsMsi;

    public Task<AdapterAssessment> AssessAsync(InstallerArtifact artifact, InstallerContext context, CancellationToken ct = default)
    {
        var info = artifact.Msi;
        var softwareName = InstallerNames.InferSoftwareName(info?.ProductName, artifact.FileDescription, artifact.FileName);
        if (info is null)
        {
            return Task.FromResult(new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                SupportsAutomaticRedirect = false,
                Compatibility = CompatibilityLevel.Unsupported,
                Strategy = "MSI 元数据不可读",
                Limitation = "无法读取 MSI 的 Property/Directory 表，禁止改写安装参数。",
                AssistedGuidance = "请在安装界面中手动选择 D 盘目标目录。"
            });
        }

        var limitation = info.IsPerMachine
            ? "按机器安装的 MSI 可能触发 UAC；重定向不会绕过 Windows Installer 权限。"
            : "按用户安装的 MSI 只修改安装目录，不迁移用户数据。";
        var hasExplicit = info.HasExplicitInstallProperty &&
                          !string.IsNullOrWhiteSpace(info.DirectoryProperty) &&
                          !string.Equals(info.DirectoryProperty, "TARGETDIR", StringComparison.OrdinalIgnoreCase);
        var canTransform = !hasExplicit &&
                           context.Settings.EnableMsiTransformFallback &&
                           !string.IsNullOrWhiteSpace(info.DirectoryProperty) &&
                           info.AnchorDirectoryId is not null &&
                           !string.IsNullOrWhiteSpace(info.DefaultInstallPath);

        if (info.IsInstalled)
        {
            return Task.FromResult(new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = info.DefaultInstallPath,
                SupportsAutomaticRedirect = false,
                Compatibility = CompatibilityLevel.High,
                Strategy = "已安装产品维护调用",
                Limitation = "检测到同一 ProductCode 已安装，遵循不迁移既有软件原则。",
                AssistedGuidance = "升级、修复或卸载将按原安装位置执行。"
            });
        }

        if (InstallerNames.ContainsPathOverride(context.OriginalArguments, artifact.Type))
        {
            return Task.FromResult(new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = info.DefaultInstallPath,
                SupportsAutomaticRedirect = false,
                Compatibility = CompatibilityLevel.High,
                Strategy = "用户已指定 MSI 目录属性",
                UserSpecifiedPathReason = "命令行已包含安装目录属性，尊重用户选择。",
                Limitation = limitation
            });
        }

        if (hasExplicit)
        {
            return Task.FromResult(new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = info.DefaultInstallPath,
                PublicDirectoryProperty = info.DirectoryProperty,
                SupportsAutomaticRedirect = true,
                Compatibility = CompatibilityLevel.High,
                Strategy = $"Windows Installer 公共属性 {info.DirectoryProperty}",
                Limitation = limitation
            });
        }

        if (canTransform)
        {
            return Task.FromResult(new AdapterAssessment
            {
                AdapterId = Id,
                AdapterVersion = Version,
                InstallerType = InstallerType,
                SoftwareName = softwareName,
                OriginalInstallPath = info.DefaultInstallPath,
                PublicDirectoryProperty = info.DirectoryProperty,
                SupportsAutomaticRedirect = true,
                Compatibility = CompatibilityLevel.Medium,
                Strategy = $"MSI Transform 重定位 Directory 表锚点 {info.AnchorDirectoryId}",
                Limitation = limitation +
                             " 此包没有显式公共目录属性，将把安装目录锚点重挂到 TARGETDIR 的 D 盘目录链，并通过 TARGETDIR=D:\\ 应用；已在 WiX 私有目录测试包实测，其他厂商私有目录 MSI 仍按 Medium 兼容级别处理。"
            });
        }

        return Task.FromResult(new AdapterAssessment
        {
            AdapterId = Id,
            AdapterVersion = Version,
            InstallerType = InstallerType,
            SoftwareName = softwareName,
            OriginalInstallPath = info.DefaultInstallPath,
            SupportsAutomaticRedirect = false,
            Compatibility = CompatibilityLevel.Medium,
            Strategy = "未找到可安全覆盖的 MSI 目录属性",
            Limitation = limitation,
            AssistedGuidance = info.DefaultInstallPath is null
                ? "请在安装界面中手动选择 D 盘目标目录。"
                : $"请在安装界面把安装位置改为 {info.DefaultInstallPath.Replace(@"C:\", @"D:\", StringComparison.OrdinalIgnoreCase)}。"
        });
    }

    public async Task<AdapterLaunchSpec> BuildLaunchAsync(
        InstallerArtifact artifact,
        AdapterAssessment assessment,
        string targetPath,
        InstallerContext context,
        CancellationToken ct = default)
    {
        var info = artifact.Msi ?? throw new InvalidOperationException("MSI 元数据不可用。");
        var sessionLog = Path.Combine(context.Paths.LogsDirectory, $"msi-{context.SessionId:D}.log");
        var args = new List<string> { "/i", InstallerArgumentQuoter.Quote(artifact.FilePath) };

        if (!string.IsNullOrWhiteSpace(context.OriginalArguments))
            args.Add(context.OriginalArguments.Trim());

        string? transformPath = null;
        var property = assessment.PublicDirectoryProperty;
        if (info.HasExplicitInstallProperty &&
            !string.IsNullOrWhiteSpace(property) &&
            !string.Equals(property, "TARGETDIR", StringComparison.OrdinalIgnoreCase))
        {
            args.Add($"{property}=\"{targetPath}\"");
        }
        else if (!string.IsNullOrWhiteSpace(property))
        {
            transformPath = await _transformBuilder.BuildAsync(
                artifact.FilePath,
                targetPath,
                property,
                info,
                context.Paths.TransformsDirectory,
                context.SessionId,
                ct);
            args.Add("TARGETDIR=\"D:\\\"");
            args.Add($"TRANSFORMS=\"{transformPath}\"");
        }
        else
        {
            throw new InvalidOperationException("没有可用于定位安装目录的 MSI 目录属性。");
        }

        if (!context.OriginalArguments.Contains("/L", StringComparison.OrdinalIgnoreCase))
            args.Add($"/L*v \"{sessionLog}\"");

        return new AdapterLaunchSpec
        {
            FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            Arguments = string.Join(' ', args),
            WorkingDirectory = Path.GetDirectoryName(artifact.FilePath) ?? Environment.SystemDirectory,
            VerificationPath = targetPath,
            LogPath = sessionLog,
            StrategyDetail = transformPath is null
                ? $"MSI 公共属性 {property}"
                : $"MSI Transform {Path.GetFileName(transformPath)}"
        };
    }
}

public sealed class WixMsiInstallerAdapter : MsiInstallerAdapter
{
    public WixMsiInstallerAdapter(MsiTransformBuilder transformBuilder)
        : base(transformBuilder, true)
    {
    }
}

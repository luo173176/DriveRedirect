using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Installers.Detection;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class AdapterRegistry
{
    private readonly IReadOnlyList<IInstallerAdapter> _adapters;
    private readonly IReadOnlyList<AdapterDescriptor> _descriptors;

    public AdapterRegistry(
        MsiTransformBuilder transformBuilder,
        InstallerProfileStore profiles)
    {
        _adapters =
        [
            new WixMsiInstallerAdapter(transformBuilder),
            new MsiInstallerAdapter(transformBuilder),
            new InnoSetupAdapter(),
            new NsisAdapter(),
            new InstallShieldAdapter(),
            new CustomExeAdapter(profiles),
            new GameLauncherAdapter(profiles),
            new MsixAdapter()
        ];

        _descriptors =
        [
            new AdapterDescriptor
            {
                Id = "msi",
                DisplayName = "Windows Installer (MSI)",
                Version = "1.0.0",
                InstallerType = InstallerType.WindowsMsi,
                Compatibility = CompatibilityLevel.High,
                TestStatus = TestStatus.Limited,
                TestSummary = "已实现 Property/Directory 表解析、公共目录属性与 MST 兜底；等待完整安装矩阵验证。",
                Strategy = "msiexec /i + 公共目录属性；无公共属性时生成 MST",
                Limitation = "不同厂商 MSI UI 是否接受公共属性存在差异；Transform 兜底为高兼容策略，不修改系统级安装器策略。"
            },
            new AdapterDescriptor
            {
                Id = "wix-msi",
                DisplayName = "WiX MSI",
                Version = "1.0.0",
                InstallerType = InstallerType.Wix,
                Compatibility = CompatibilityLevel.High,
                TestStatus = TestStatus.Limited,
                TestSummary = "识别 WiX MSI 并复用 MSI 原生目录属性/MST 策略；等待真实 WiX 包安装验证。",
                Strategy = "MSI 原生机制，无进程注入",
                Limitation = "WiX Burn EXE 引导程序不按 MSI 方式处理，需按自定义安装器或专用适配配置。"
            },
            new AdapterDescriptor
            {
                Id = "inno-setup",
                DisplayName = "Inno Setup",
                Version = "1.0.0",
                InstallerType = InstallerType.InnoSetup,
                Compatibility = CompatibilityLevel.High,
                TestStatus = TestStatus.Limited,
                TestSummary = "使用官方 /DIR 参数覆盖默认目录；等待测试安装包实测。",
                Strategy = "Inno Setup /DIR，保留安装界面",
                Limitation = "默认目录来自编译脚本，展示值为应用名推断；用户显式传入 /DIR 时不覆盖。"
            },
            new AdapterDescriptor
            {
                Id = "nsis",
                DisplayName = "NSIS",
                Version = "1.0.0",
                InstallerType = InstallerType.Nsis,
                Compatibility = CompatibilityLevel.High,
                TestStatus = TestStatus.Limited,
                TestSummary = "使用官方 /D 参数且置于命令行末尾；等待测试安装包实测。",
                Strategy = "NSIS /D，保留安装界面",
                Limitation = "仅标准 $INSTDIR 安装流程适用；自定义页面或忽略 /D 的安装器会被辅助/安全放行。"
            },
            new AdapterDescriptor
            {
                Id = "installshield",
                DisplayName = "InstallShield",
                Version = "1.0.0",
                InstallerType = InstallerType.InstallShield,
                Compatibility = CompatibilityLevel.Medium,
                TestStatus = TestStatus.Limited,
                TestSummary = "识别 InstallShield；仅对检测到 Basic MSI 的安装包传递 INSTALLDIR，其余辅助放行。",
                Strategy = "Basic MSI /v 传递；InstallScript 安全放行",
                Limitation = "InstallShield 模式差异大，未确认模式时不注入参数。"
            },
            new AdapterDescriptor
            {
                Id = "custom-exe",
                DisplayName = "自定义 EXE 安装器",
                Version = "1.0.0",
                InstallerType = InstallerType.CustomExe,
                Compatibility = CompatibilityLevel.Medium,
                TestStatus = TestStatus.Limited,
                TestSummary = "仅使用经用户启用且验证的参数模板；未知 EXE 不注入参数。",
                Strategy = "白名单参数模板 + 哈希/文件名匹配",
                Limitation = "每个自定义安装器必须提供并验证专属配置。"
            },
            new AdapterDescriptor
            {
                Id = "game-launcher",
                DisplayName = "游戏启动器",
                Version = "1.0.0",
                InstallerType = InstallerType.GameLauncher,
                Compatibility = CompatibilityLevel.Medium,
                TestStatus = TestStatus.Limited,
                TestSummary = "识别常见启动器；默认辅助放行，仅验证过的启动器配置可自动重定向。",
                Strategy = "识别 + 可选验证配置",
                Limitation = "游戏库目录通常由启动器自身管理，安装器路径改写不等于游戏库迁移。"
            },
            new AdapterDescriptor
            {
                Id = "msix",
                DisplayName = "MSIX / AppX",
                Version = "1.0.0",
                InstallerType = InstallerType.Msix,
                Compatibility = CompatibilityLevel.Unsupported,
                TestStatus = TestStatus.Limited,
                TestSummary = "遵循 Windows 官方部署机制，不改写系统管理的包位置。",
                Strategy = "安全放行",
                Limitation = "MSIX/AppX 安装位置由 Windows 部署子系统管理。"
            }
        ];
    }

    public IInstallerAdapter? Resolve(InstallerArtifact artifact)
        => _adapters.FirstOrDefault(a => a.CanHandle(artifact));

    public IInstallerAdapter? GetById(string? id)
        => id is null ? null : _adapters.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<AdapterDescriptor> GetDescriptors() => _descriptors;

    public IReadOnlyList<CompatibilityItem> GetCompatibilityItems(
        IReadOnlyDictionary<InstallerType, (int Count, DateTimeOffset? LastObserved)> observations)
    {
        return _descriptors.Select(d =>
        {
            var observed = observations.TryGetValue(d.InstallerType, out var value) ? value : default;
            return new CompatibilityItem
            {
                AdapterId = d.Id,
                DisplayName = d.DisplayName,
                AdapterVersion = d.Version,
                InstallerType = d.InstallerType,
                Compatibility = d.Compatibility,
                TestStatus = d.TestStatus,
                TestSummary = d.TestSummary,
                Strategy = d.Strategy,
                Limitation = d.Limitation,
                LastObservedAt = observed.LastObserved,
                ObservedCount = observed.Count
            };
        }).ToList();
    }
}

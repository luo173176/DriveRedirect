namespace DriveRedirect.Core.Models;

public sealed class InstallerArtifact
{
    public required string FilePath { get; init; }
    public required InstallerType Type { get; init; }
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? ProductVersion { get; init; }
    public string? FileDescription { get; init; }
    public string? Sha256 { get; init; }
    public MsiPackageInfo? Msi { get; init; }
    public IReadOnlyList<string> DetectionSignals { get; init; } = [];
    public bool IsInstaller => Type != InstallerType.Unknown;
}

public sealed class MsiPackageInfo
{
    public string? ProductName { get; init; }
    public string? Manufacturer { get; init; }
    public string? ProductVersion { get; init; }
    public string? ProductCode { get; init; }
    public string? UpgradeCode { get; init; }
    public bool IsPerMachine { get; init; }
    public bool IsInstalled { get; init; }
    public string? DirectoryProperty { get; init; }
    public string? DefaultInstallPath { get; init; }
    public string? ResolvedDirectoryPath { get; init; }
    public string? AnchorDirectoryId { get; init; }
    public string? AnchorDirectoryPath { get; init; }
    public string? BaseDirectoryId { get; init; }
    public IReadOnlyList<MsiDirectoryNode> InstallDirectoryChain { get; init; } = [];
    public bool HasExplicitInstallProperty { get; init; }
    public IReadOnlyList<string> PublicDirectoryProperties { get; init; } = [];
    public IReadOnlyDictionary<string, string> AllProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class MsiDirectoryNode
{
    public required string DirectoryId { get; init; }
    public string? ParentId { get; init; }
    public string? DefaultDir { get; init; }
    public string? Path { get; init; }
    public bool IsBaseFolder { get; init; }
}

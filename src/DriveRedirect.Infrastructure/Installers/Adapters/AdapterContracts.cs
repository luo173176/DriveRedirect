using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Configuration;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class InstallerContext
{
    public required string OriginalArguments { get; init; }
    public required AppSettings Settings { get; init; }
    public required AppPaths Paths { get; init; }
    public Guid SessionId { get; init; }
    public string? UserSid { get; init; }
    public bool IsMaintenance { get; init; }
    public bool HasUserSpecifiedPath { get; init; }
}

public sealed class AdapterLaunchSpec
{
    public required string FileName { get; init; }
    public required string Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? VerificationPath { get; init; }
    public string? LogPath { get; init; }
    public string? StrategyDetail { get; init; }
}

public interface IInstallerAdapter
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }
    InstallerType InstallerType { get; }
    bool CanHandle(InstallerArtifact artifact);
    Task<AdapterAssessment> AssessAsync(InstallerArtifact artifact, InstallerContext context, CancellationToken ct = default);
    Task<AdapterLaunchSpec> BuildLaunchAsync(
        InstallerArtifact artifact,
        AdapterAssessment assessment,
        string targetPath,
        InstallerContext context,
        CancellationToken ct = default);
}

public sealed class AdapterDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public InstallerType InstallerType { get; init; }
    public CompatibilityLevel Compatibility { get; init; }
    public TestStatus TestStatus { get; init; }
    public required string TestSummary { get; init; }
    public required string Strategy { get; init; }
    public string? Limitation { get; init; }
}

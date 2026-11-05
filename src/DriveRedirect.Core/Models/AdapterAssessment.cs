namespace DriveRedirect.Core.Models;

public sealed class AdapterAssessment
{
    public required string AdapterId { get; init; }
    public required string AdapterVersion { get; init; }
    public required InstallerType InstallerType { get; init; }
    public required string SoftwareName { get; init; }
    public string? OriginalInstallPath { get; init; }
    public string? PublicDirectoryProperty { get; init; }
    public bool SupportsAutomaticRedirect { get; init; }
    public CompatibilityLevel Compatibility { get; init; }
    public required string Strategy { get; init; }
    public string? UserSpecifiedPathReason { get; init; }
    public string? Limitation { get; init; }
    public string? AssistedGuidance { get; init; }
}

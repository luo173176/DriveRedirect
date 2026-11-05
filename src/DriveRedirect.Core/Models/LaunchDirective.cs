namespace DriveRedirect.Core.Models;

public sealed class LaunchDirective
{
    public Guid SessionId { get; init; }
    public bool Redirected { get; init; }
    public HandlingMode Mode { get; init; }
    public required string FileName { get; init; }
    public required string Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public InstallerType InstallerType { get; init; }
    public string? SoftwareName { get; init; }
    public string? InstallerPath { get; init; }
    public string? OriginalInstallPath { get; init; }
    public string? TargetInstallPath { get; init; }
    public string? VerificationPath { get; init; }
    public string? UserMessage { get; init; }
    public string? Reason { get; init; }
    public string? AdapterId { get; init; }
    public string? AdapterVersion { get; init; }
    public string? Strategy { get; init; }
    public string? AssistedGuidance { get; init; }
    public CompatibilityLevel Compatibility { get; init; }
    public string? LogPath { get; init; }
}

public sealed class LaunchPlanRequest
{
    public required InstallerLaunchKind LaunchKind { get; init; }
    public required string TargetPath { get; init; }
    public string OriginalArguments { get; init; } = "";
    public string? UserSid { get; init; }
}

public sealed class ProcessStartedRequest
{
    public Guid SessionId { get; init; }
    public int ProcessId { get; init; }
}

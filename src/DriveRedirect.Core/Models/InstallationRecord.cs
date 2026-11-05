namespace DriveRedirect.Core.Models;

public sealed class InstallationRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? EndedAt { get; set; }
    public InstallerType InstallerType { get; set; }
    public required string SoftwareName { get; set; }
    public string? InstallerPath { get; set; }
    public string? InstallerVersion { get; set; }
    public string? InstallerHash { get; set; }
    public int? ProcessId { get; set; }
    public int? ParentProcessId { get; set; }
    public string? OriginalPath { get; set; }
    public string? TargetPath { get; set; }
    public string? ActualPath { get; set; }
    public HandlingMode Mode { get; set; }
    public CompatibilityLevel Compatibility { get; set; }
    public InstallationStatus Status { get; set; }
    public string? AdapterId { get; set; }
    public string? AdapterVersion { get; set; }
    public string? Strategy { get; set; }
    public string? LaunchArguments { get; set; }
    public string? MsiLogPath { get; set; }
    public int? ExitCode { get; set; }
    public string? FailureReason { get; set; }
    public string? VerificationSource { get; set; }
    public bool WasRedirected { get; set; }
    public string? UserSid { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }
}

public sealed class LiveInstallSession
{
    public Guid Id { get; init; }
    public string SoftwareName { get; init; } = "";
    public InstallerType InstallerType { get; init; }
    public string? InstallerPath { get; init; }
    public int? ProcessId { get; init; }
    public string? OriginalPath { get; init; }
    public string? TargetPath { get; init; }
    public InstallationStatus Status { get; init; }
    public HandlingMode Mode { get; init; }
    public CompatibilityLevel Compatibility { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string? AdapterId { get; init; }
    public string? StatusDetail { get; init; }
    public bool WasRedirected { get; init; }
}

public sealed class HistoryQuery
{
    public string? Search { get; init; }
    public string? ResultFilter { get; init; }
    public string? InstallerFilter { get; init; }
    public string SortBy { get; init; } = "date";
    public bool Descending { get; init; } = true;
    public int Skip { get; init; }
    public int Take { get; init; } = 100;
}

public sealed class HistoryResult
{
    public IReadOnlyList<InstallationRecord> Items { get; init; } = [];
    public int Total { get; init; }
}

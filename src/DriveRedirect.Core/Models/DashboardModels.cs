namespace DriveRedirect.Core.Models;

public sealed class DashboardSnapshot
{
    public DiskInfo? SystemDisk { get; init; }
    public DiskInfo? TargetDisk { get; init; }
    public bool AutomaticRedirectEnabled { get; init; }
    public bool MonitoringActive { get; init; }
    public bool ServiceOnline { get; init; }
    public string ServiceVersion { get; init; } = "";
    public int TodayInstalls { get; init; }
    public int TotalRedirects { get; init; }
    public int TodayRedirects { get; init; }
    public ShellIntegrationState ShellIntegration { get; init; }
    public string? ShellIntegrationDetail { get; init; }
    public IReadOnlyList<InstallationRecord> RecentInstallations { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class CompatibilityItem
{
    public required string AdapterId { get; init; }
    public required string DisplayName { get; init; }
    public required string AdapterVersion { get; init; }
    public InstallerType InstallerType { get; init; }
    public CompatibilityLevel Compatibility { get; init; }
    public TestStatus TestStatus { get; init; }
    public required string TestSummary { get; init; }
    public required string Strategy { get; init; }
    public string? Limitation { get; init; }
    public DateTimeOffset? LastObservedAt { get; init; }
    public int ObservedCount { get; init; }
}

public sealed class ObservedInstaller
{
    public Guid Id { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public required string InstallerPath { get; init; }
    public InstallerType InstallerType { get; init; }
    public string? SoftwareName { get; init; }
    public string? AdapterId { get; init; }
    public CompatibilityLevel Compatibility { get; init; }
    public HandlingMode Mode { get; init; }
    public string? Reason { get; init; }
    public string? Hash { get; init; }
}

public sealed class DiagnosticsSnapshot
{
    public bool ServiceOnline { get; init; }
    public string ServiceVersion { get; init; } = "";
    public DateTimeOffset ServiceStartedAt { get; init; }
    public string DataDirectory { get; init; } = "";
    public long DatabaseBytes { get; init; }
    public long LogsBytes { get; init; }
    public int PendingSessions { get; init; }
    public int ExceptionCount24h { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<LogFileInfo> LogFiles { get; init; } = [];
    public ShellIntegrationState ShellIntegration { get; init; }
    public string? ShellIntegrationDetail { get; init; }
}

public sealed class LogFileInfo
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long Length { get; init; }
    public DateTimeOffset LastWriteTime { get; init; }
}

public sealed class ServiceResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public static ServiceResult Ok(string? message = null) => new() { Success = true, Message = message };
    public static ServiceResult Fail(string message) => new() { Success = false, Message = message };
}

public sealed class ExportLogsRequest
{
    public string? DestinationPath { get; init; }
}

public sealed class DashboardSummary
{
    public int TodayInstalls { get; init; }
    public int TodayRedirects { get; init; }
    public int TotalRedirects { get; init; }
}

public sealed class ShellIntegrationResult
{
    public bool Success { get; init; }
    public ShellIntegrationState State { get; init; }
    public required string Detail { get; init; }
}

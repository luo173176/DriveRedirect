namespace DriveRedirect.Core.Models;

public sealed class PathRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public RuleKind Kind { get; set; }
    public required string SourcePattern { get; set; }
    public string? TargetPattern { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public bool AllowAppData { get; set; }
    public string? Note { get; set; }
    public bool IsBuiltIn { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class AppSettings
{
    public bool AutomaticRedirectEnabled { get; set; } = true;
    public bool BackgroundMonitoringEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool ExplorerLaunchInterception { get; set; } = true;
    public bool EnableMsiTransformFallback { get; set; } = true;
    public bool AllowAppDataRedirection { get; set; }
    public bool VerifyAfterInstall { get; set; } = true;
    public string TargetDrive { get; set; } = "D:";
    public string Theme { get; set; } = "System";
    public string LogLevel { get; set; } = "Information";
    public int MinimumFreeSpaceGb { get; set; } = 2;
    public int HistoryRetentionDays { get; set; }
}

public sealed class DiskProbeResult
{
    public required string Root { get; init; }
    public bool Exists { get; init; }
    public bool IsReady { get; init; }
    public bool IsWritable { get; init; }
    public bool IsFixed { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public string? FileSystem { get; init; }
    public string? Error { get; init; }
}

public sealed class PathEvaluationResult
{
    public bool Allowed { get; init; }
    public required string OriginalPath { get; init; }
    public string? TargetPath { get; init; }
    public string? MatchedRuleName { get; init; }
    public required string Reason { get; init; }
    public bool RequiresExplicitAppDataConsent { get; init; }
}

public sealed class DiskInfo
{
    public required string Name { get; init; }
    public required string Root { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public double UsagePercent => TotalBytes <= 0 ? 0 : Math.Round((TotalBytes - FreeBytes) * 100d / TotalBytes, 1);
    public bool IsReady { get; init; }
    public string? Label { get; init; }
    public string? Error { get; init; }
}

public interface IDiskProbe
{
    DiskProbeResult Probe(string rootOrDrive);
}

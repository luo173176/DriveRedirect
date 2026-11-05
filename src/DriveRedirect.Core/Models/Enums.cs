namespace DriveRedirect.Core.Models;

public enum InstallerType
{
    Unknown = 0,
    WindowsMsi = 1,
    Wix = 2,
    InnoSetup = 3,
    Nsis = 4,
    InstallShield = 5,
    CustomExe = 6,
    Msix = 7,
    GameLauncher = 8
}

public enum HandlingMode
{
    Automatic = 0,
    Assisted = 1,
    PassThrough = 2
}

public enum CompatibilityLevel
{
    Unsupported = 0,
    Experimental = 1,
    Medium = 2,
    High = 3,
    Verified = 4
}

public enum InstallationStatus
{
    Planned = 0,
    Launching = 1,
    Running = 2,
    Succeeded = 3,
    Redirected = 4,
    Failed = 5,
    Cancelled = 6,
    Interrupted = 7,
    PassedThrough = 8,
    Assisted = 9,
    Unverified = 10
}

public enum RuleKind
{
    Mapping = 0,
    AppWhitelist = 1,
    AppBlacklist = 2,
    ProtectedPath = 3
}

public enum InstallerLaunchKind
{
    Exe = 0,
    Msi = 1
}

public enum ShellIntegrationState
{
    Disabled = 0,
    Enabled = 1,
    Partial = 2,
    Unavailable = 3
}

public enum TestStatus
{
    NotTested = 0,
    Passed = 1,
    Failed = 2,
    Limited = 3
}

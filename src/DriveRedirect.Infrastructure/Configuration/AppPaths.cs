namespace DriveRedirect.Infrastructure.Configuration;

public sealed class AppPaths
{
    public AppPaths(string? overrideRoot = null)
    {
        var root = overrideRoot;
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetEnvironmentVariable("DRIVEREDIRECT_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DriveRedirect");

        DataDirectory = Path.GetFullPath(root);
        DatabasePath = Path.Combine(DataDirectory, "driveredirect.db");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        TransformsDirectory = Path.Combine(DataDirectory, "transforms");
        IpcTokenPath = Path.Combine(DataDirectory, "ipc.token");
        ShellBackupPath = Path.Combine(DataDirectory, "shell-backup.json");
        ExportDirectory = Path.Combine(DataDirectory, "exports");
        ProfilesPath = Path.Combine(DataDirectory, "installer-profiles.json");
        LauncherPath = Path.Combine(AppContext.BaseDirectory, "DriveRedirect.Launcher.exe");
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string LogsDirectory { get; }
    public string TransformsDirectory { get; }
    public string IpcTokenPath { get; }
    public string ShellBackupPath { get; }
    public string ExportDirectory { get; }
    public string ProfilesPath { get; }
    public string LauncherPath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TransformsDirectory);
        Directory.CreateDirectory(ExportDirectory);
    }

    public static string LocalLauncherLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DriveRedirect",
        "launcher.log");
}

using System.Security.Principal;
using System.Text.Json;
using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Configuration;
using Microsoft.Win32;

namespace DriveRedirect.Infrastructure.Shell;

public sealed class ShellIntegrationManager
{
    private const string ExeCommandKey = @"SOFTWARE\Classes\exefile\shell\open\command";
    private const string MsiCommandKey = @"SOFTWARE\Classes\Msi.Package\shell\open\command";
    private const string DefaultExeCommand = "\"%1\" %*";
    private const string DefaultMsiCommand = "\"%SystemRoot%\\System32\\msiexec.exe\" /i \"%1\" %*";

    private readonly AppPaths _paths;

    public ShellIntegrationManager(AppPaths paths)
    {
        _paths = paths;
    }

    public ShellIntegrationResult GetState()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var exeKey = hklm.OpenSubKey(ExeCommandKey);
            using var msiKey = hklm.OpenSubKey(MsiCommandKey);
            var exeCommand = exeKey?.GetValue(null) as string ?? "";
            var msiCommand = msiKey?.GetValue(null) as string ?? "";
            var launcher = _paths.LauncherPath;
            var exeOurs = exeCommand.Contains(launcher, StringComparison.OrdinalIgnoreCase) &&
                          exeCommand.Contains("--exe", StringComparison.OrdinalIgnoreCase);
            var msiOurs = msiCommand.Contains(launcher, StringComparison.OrdinalIgnoreCase) &&
                          msiCommand.Contains("--msi", StringComparison.OrdinalIgnoreCase);

            var state = exeOurs && msiOurs
                ? ShellIntegrationState.Enabled
                : exeOurs || msiOurs
                    ? ShellIntegrationState.Partial
                    : ShellIntegrationState.Disabled;

            var detail = state switch
            {
                ShellIntegrationState.Enabled => "资源管理器 EXE/MSI 双击将由安装启动拦截器接管。",
                ShellIntegrationState.Partial => "仅部分安装包类型处于接管状态。",
                _ => "资源管理器双击接管未启用。"
            };
            var userChoice = ReadMsiUserChoice();
            if (!string.IsNullOrWhiteSpace(userChoice) &&
                !userChoice.Equals("Msi.Package", StringComparison.OrdinalIgnoreCase))
                detail += $" 当前用户 .msi UserChoice 为 {userChoice}，可能绕过通用 MSI 处理程序。";

            return new ShellIntegrationResult { Success = true, State = state, Detail = detail };
        }
        catch (Exception ex)
        {
            return new ShellIntegrationResult
            {
                Success = false,
                State = ShellIntegrationState.Unavailable,
                Detail = "无法读取关联状态：" + ex.Message
            };
        }
    }

    public ShellIntegrationResult Enable()
    {
        if (!IsAdministrator())
            return Failure("启用全局安装启动拦截需要管理员权限。");
        if (!File.Exists(_paths.LauncherPath))
            return Failure($"启动拦截器不存在：{_paths.LauncherPath}");

        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            var current = GetState();
            if (current.State != ShellIntegrationState.Enabled)
                BackupCurrentCommands();

            var exeCommand = $"\"{_paths.LauncherPath}\" --exe \"%1\" %*";
            var msiCommand = $"\"{_paths.LauncherPath}\" --msi \"%1\" %*";
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var exeKey = hklm.CreateSubKey(ExeCommandKey, true))
                exeKey.SetValue(null, exeCommand, RegistryValueKind.String);
            using (var msiKey = hklm.CreateSubKey(MsiCommandKey, true))
                msiKey.SetValue(null, msiCommand, RegistryValueKind.String);

            // Broadcast the association change without requiring a logout.
            NativeMethods.SendMessageTimeout(
                new IntPtr(0xffff),
                0x001A,
                IntPtr.Zero,
                "Software\\Classes",
                0x0002,
                3000,
                out _);

            var state = GetState();
            return new ShellIntegrationResult
            {
                Success = state.State == ShellIntegrationState.Enabled,
                State = state.State,
                Detail = state.Detail
            };
        }
        catch (Exception ex)
        {
            return Failure("启用资源管理器启动拦截失败：" + ex.Message);
        }
    }

    public ShellIntegrationResult Disable()
    {
        if (!IsAdministrator())
            return Failure("恢复资源管理器关联需要管理员权限。");
        try
        {
            var exeCommand = DefaultExeCommand;
            var msiCommand = DefaultMsiCommand;
            if (File.Exists(_paths.ShellBackupPath))
            {
                try
                {
                    var backup = JsonSerializer.Deserialize<ShellBackup>(
                        File.ReadAllText(_paths.ShellBackupPath));
                    if (!string.IsNullOrWhiteSpace(backup?.ExeCommand))
                        exeCommand = backup.ExeCommand;
                    if (!string.IsNullOrWhiteSpace(backup?.MsiCommand))
                        msiCommand = backup.MsiCommand;
                }
                catch
                {
                    // Fall back to the Windows defaults.
                }
            }

            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var exeKey = hklm.CreateSubKey(ExeCommandKey, true))
                exeKey.SetValue(null, exeCommand, RegistryValueKind.String);
            using (var msiKey = hklm.CreateSubKey(MsiCommandKey, true))
                msiKey.SetValue(null, msiCommand, RegistryValueKind.String);

            if (File.Exists(_paths.ShellBackupPath))
            {
                try
                {
                    File.Move(_paths.ShellBackupPath,
                        _paths.ShellBackupPath + $".restored-{DateTime.Now:yyyyMMddHHmmss}",
                        true);
                }
                catch
                {
                    // The restored association is more important than the audit copy.
                }
            }

            NativeMethods.SendMessageTimeout(
                new IntPtr(0xffff),
                0x001A,
                IntPtr.Zero,
                "Software\\Classes",
                0x0002,
                3000,
                out _);

            var state = GetState();
            return new ShellIntegrationResult
            {
                Success = state.State == ShellIntegrationState.Disabled,
                State = state.State,
                Detail = state.Detail
            };
        }
        catch (Exception ex)
        {
            return Failure("恢复资源管理器关联失败：" + ex.Message);
        }
    }

    private void BackupCurrentCommands()
    {
        if (File.Exists(_paths.ShellBackupPath))
            return;

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var exeKey = hklm.OpenSubKey(ExeCommandKey);
        using var msiKey = hklm.OpenSubKey(MsiCommandKey);
        var backup = new ShellBackup
        {
            CreatedAt = DateTimeOffset.Now,
            ExeCommand = exeKey?.GetValue(null) as string ?? DefaultExeCommand,
            MsiCommand = msiKey?.GetValue(null) as string ?? DefaultMsiCommand,
            LauncherPath = _paths.LauncherPath
        };
        File.WriteAllText(_paths.ShellBackupPath, JsonSerializer.Serialize(backup, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static string? ReadMsiUserChoice()
    {
        try
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = hkcu.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.msi\UserChoice");
            return key?.GetValue("ProgId") as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static ShellIntegrationResult Failure(string message) => new()
    {
        Success = false,
        State = ShellIntegrationState.Unavailable,
        Detail = message
    };

    private sealed class ShellBackup
    {
        public DateTimeOffset CreatedAt { get; set; }
        public string? ExeCommand { get; set; }
        public string? MsiCommand { get; set; }
        public string? LauncherPath { get; set; }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out IntPtr result);
    }
}

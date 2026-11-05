using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Launcher;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DriveRedirect",
        "launcher.log");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return 0;

            Log("启动拦截器收到命令：" + string.Join(' ', args.Select(QuoteForLog)));
            if (args[0].Equals("--restore-shell", StringComparison.OrdinalIgnoreCase))
                return RestoreShell(requireElevated: true);
            if (args[0].Equals("--restore-shell-elevated", StringComparison.OrdinalIgnoreCase))
                return RestoreShell(requireElevated: false);
            if (args[0].Equals("--restore-shell-quiet", StringComparison.OrdinalIgnoreCase))
                return RestoreShell(requireElevated: false, quiet: true);

            if (args[0].Equals("--exe", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("--msi", StringComparison.OrdinalIgnoreCase))
            {
                var isMsi = args[0].Equals("--msi", StringComparison.OrdinalIgnoreCase);
                if (args.Length < 2)
                    return Fail("启动参数缺少安装器路径。");
                var target = Path.GetFullPath(args[1].Trim().Trim('"'));
                if (!File.Exists(target))
                    return Fail("安装器文件不存在：" + target);
                var originalArguments = BuildOriginalArguments(args, 2);
                return await RunInterceptedAsync(target, originalArguments, isMsi);
            }

            if (args[0].Equals("--direct", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
            {
                var target = args[1];
                var arguments = BuildOriginalArguments(args, 2);
                var process = StartProcess(target, arguments, Path.GetDirectoryName(target) ?? Environment.CurrentDirectory);
                return process is null ? 1 : 0;
            }

            return Fail("未知的启动拦截命令。");
        }
        catch (Exception ex)
        {
            Log("未处理异常：" + ex);
            return 1;
        }
    }

    private static async Task<int> RunInterceptedAsync(string target, string originalArguments, bool isMsi)
    {
        var candidate = isMsi || LaunchCandidateDetector.IsCandidate(target, out _);
        if (!candidate)
        {
            var process = StartProcess(target, originalArguments, Path.GetDirectoryName(target) ?? Environment.CurrentDirectory);
            return process is null ? 1 : 0;
        }

        LaunchDirective? directive = null;
        string? planError = null;
        try
        {
            directive = await RequestPlanAsync(target, originalArguments, isMsi);
        }
        catch (Exception ex)
        {
            planError = ex.Message;
            Log("获取重定向计划失败：" + ex);
        }

        if (directive is null)
        {
            ShowMessage(
                "DriveRedirect 后台服务暂不可用，安装器将以原始设置启动。\n\n" +
                "错误：" + (planError ?? "没有可用计划") + "\n\n" +
                "请确认 DriveRedirect Service 正在运行，然后重试以启用自动路径重定向。",
                "DriveRedirect");
            var fallback = StartRaw(target, originalArguments, isMsi);
            return fallback ? 0 : 1;
        }

        if (directive.Mode == HandlingMode.Assisted &&
            !string.IsNullOrWhiteSpace(directive.UserMessage))
        {
            var answer = ShowYesNo(directive.UserMessage, "DriveRedirect · 辅助安装");
            if (answer != 6) // IDYES
            {
                Log("用户在辅助提示中选择取消。");
                return 0;
            }
        }

        Log($"计划：redirect={directive.Redirected} mode={directive.Mode} target={directive.TargetInstallPath} launch={directive.FileName}");
        var launched = StartProcess(
            directive.FileName,
            directive.Arguments,
            Directory.Exists(directive.WorkingDirectory)
                ? directive.WorkingDirectory
                : Path.GetDirectoryName(directive.FileName) ?? Environment.CurrentDirectory);
        if (launched is null)
        {
            ShowMessage("安装器启动失败。请检查安装包完整性或尝试直接运行安装包。", "DriveRedirect");
            return 1;
        }

        if (directive.SessionId != Guid.Empty)
            await ReportProcessStartedAsync(directive.SessionId, launched.Id);
        Log($"已启动安装器 pid={launched.Id} file={directive.FileName}");
        return 0;
    }

    private static async Task<LaunchDirective?> RequestPlanAsync(string target, string originalArguments, bool isMsi)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            IpcNames.BrokerPipe,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await pipe.ConnectAsync(connectTimeout.Token);

        var requestId = Guid.NewGuid().ToString("D");
        var requestJson = BuildPlanRequestJson(requestId, target, originalArguments, isMsi);
        var bytes = Encoding.UTF8.GetBytes(requestJson + "\n");
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(readTimeout.Token);
        if (string.IsNullOrWhiteSpace(responseLine))
            throw new IOException("后台服务没有返回重定向计划。");

        using var document = JsonDocument.Parse(responseLine);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : "未知 IPC 错误";
            throw new InvalidOperationException(error);
        }
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("后台响应缺少计划内容。");

        return new LaunchDirective
        {
            SessionId = ReadGuid(payload, "sessionId"),
            FileName = ReadString(payload, "fileName") ?? target,
            Arguments = ReadString(payload, "arguments") ?? originalArguments,
            WorkingDirectory = ReadString(payload, "workingDirectory") ?? Path.GetDirectoryName(target) ?? "",
            Redirected = payload.TryGetProperty("redirected", out var redirected) && redirected.GetBoolean(),
            Mode = ParseHandlingMode(ReadString(payload, "mode")),
            UserMessage = ReadString(payload, "userMessage"),
            TargetInstallPath = ReadString(payload, "targetInstallPath"),
            OriginalInstallPath = ReadString(payload, "originalInstallPath"),
            Reason = ReadString(payload, "reason"),
            InstallerType = ParseInstallerType(ReadString(payload, "installerType")),
            Compatibility = ParseCompatibility(ReadString(payload, "compatibility"))
        };
    }

    private static async Task ReportProcessStartedAsync(Guid sessionId, int processId)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                IpcNames.BrokerPipe,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await pipe.ConnectAsync(connectTimeout.Token);

            var requestId = Guid.NewGuid().ToString("D");
            using var memory = new MemoryStream();
            using (var writer = new Utf8JsonWriter(memory))
            {
                writer.WriteStartObject();
                writer.WriteString("id", requestId);
                writer.WriteString("operation", IpcOperations.ReportProcessStarted);
                writer.WriteStartObject("payload");
                writer.WriteString("sessionId", sessionId);
                writer.WriteNumber("processId", processId);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            memory.WriteByte((byte)'\n');
            await pipe.WriteAsync(memory.ToArray());
            await pipe.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            await reader.ReadLineAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            Log("报告安装器进程失败（不影响安装）：" + ex.Message);
        }
    }

    private static string BuildPlanRequestJson(string requestId, string target, string originalArguments, bool isMsi)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject();
            writer.WriteString("id", requestId);
            writer.WriteString("operation", IpcOperations.PlanLaunch);
            writer.WriteStartObject("payload");
            writer.WriteString("launchKind", isMsi ? "msi" : "exe");
            writer.WriteString("targetPath", target);
            writer.WriteString("originalArguments", originalArguments);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static Process? StartProcess(string fileName, string arguments, string workingDirectory)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
                UseShellExecute = false
            };
            return Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            try
            {
                var elevated = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                return Process.Start(elevated);
            }
            catch (Win32Exception uacEx) when (uacEx.NativeErrorCode == 1223)
            {
                Log("用户取消了 UAC 提权。");
                return null;
            }
        }
        catch (Exception ex)
        {
            Log($"启动 {fileName} 失败：{ex}");
            return null;
        }
    }

    private static bool StartRaw(string target, string originalArguments, bool isMsi)
    {
        if (isMsi)
        {
            var args = $"/i \"{target}\"" + (string.IsNullOrWhiteSpace(originalArguments) ? "" : " " + originalArguments);
            return StartProcess(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), args, Environment.SystemDirectory) is not null;
        }
        return StartProcess(target, originalArguments, Path.GetDirectoryName(target) ?? Environment.CurrentDirectory) is not null;
    }

    private static string BuildOriginalArguments(string[] args, int startIndex)
    {
        if (args.Length <= startIndex)
            return "";
        return string.Join(' ', args.Skip(startIndex).Select(InstallerArgumentQuoter.Quote));
    }

    private static int RestoreShell(bool requireElevated, bool quiet = false)
    {
        if (requireElevated && !IsAdministrator())
        {
            try
            {
                var self = Environment.ProcessPath ?? "";
                var info = new ProcessStartInfo
                {
                    FileName = self,
                    Arguments = "--restore-shell-elevated",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(info);
                return 0;
            }
            catch (Exception ex)
            {
                Log("请求管理员权限恢复关联失败：" + ex);
                if (!quiet)
                    ShowMessage("恢复资源管理器启动关联需要管理员权限。请右键以管理员身份运行本程序并选择“以管理员身份运行”。", "DriveRedirect");
                return 1;
            }
        }

        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DriveRedirect");
            var backupPath = Path.Combine(dataDir, "shell-backup.json");
            var exeCommand = "\"%1\" %*";
            var msiCommand = "\"%SystemRoot%\\System32\\msiexec.exe\" /i \"%1\" %*";
            if (File.Exists(backupPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(backupPath));
                if (document.RootElement.TryGetProperty("exeCommand", out var exe) && exe.GetString() is { Length: > 0 } exeValue)
                    exeCommand = exeValue;
                if (document.RootElement.TryGetProperty("msiCommand", out var msi) && msi.GetString() is { Length: > 0 } msiValue)
                    msiCommand = msiValue;
            }

            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using (var key = hklm.CreateSubKey(@"SOFTWARE\Classes\exefile\shell\open\command", true))
                key.SetValue(null, exeCommand);
            using (var key = hklm.CreateSubKey(@"SOFTWARE\Classes\Msi.Package\shell\open\command", true))
                key.SetValue(null, msiCommand);

            if (!quiet)
                ShowMessage("资源管理器 EXE/MSI 启动关联已恢复。", "DriveRedirect");
            Log("已恢复资源管理器启动关联。");
            return 0;
        }
        catch (Exception ex)
        {
            Log("恢复资源管理器关联失败：" + ex);
            if (!quiet)
                ShowMessage("恢复失败：" + ex.Message, "DriveRedirect");
            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid ReadGuid(JsonElement element, string name)
        => Guid.TryParse(ReadString(element, name), out var value) ? value : Guid.Empty;

    private static InstallerType ParseInstallerType(string? value)
        => Enum.TryParse<InstallerType>(value, true, out var type) ? type : InstallerType.Unknown;

    private static CompatibilityLevel ParseCompatibility(string? value)
        => Enum.TryParse<CompatibilityLevel>(value, true, out var level) ? level : CompatibilityLevel.Unsupported;

    private static HandlingMode ParseHandlingMode(string? value)
        => Enum.TryParse<HandlingMode>(value, true, out var mode) ? mode : HandlingMode.PassThrough;

    private static string QuoteForLog(string value)
        => value.Contains(' ') ? "\"" + value + "\"" : value;

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
            {
                var old = LogPath + ".old";
                File.Move(LogPath, old, true);
            }
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static int Fail(string message)
    {
        Log(message);
        ShowMessage(message, "DriveRedirect");
        return 1;
    }

    private static void ShowMessage(string message, string caption)
    {
        try
        {
            NativeMethods.MessageBoxW(IntPtr.Zero, message, caption, 0x00000000 | 0x00000040 | 0x00040000);
        }
        catch
        {
        }
    }

    private static int ShowYesNo(string message, string caption)
    {
        try
        {
            return NativeMethods.MessageBoxW(IntPtr.Zero, message, caption, 0x00000004 | 0x00000040 | 0x00040000 | 0x00010000);
        }
        catch
        {
            return 6;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}

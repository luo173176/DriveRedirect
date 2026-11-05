using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "ping":
                    return await AdminAsync(IpcOperations.Ping, null);
                case "dashboard":
                    return await AdminAsync(IpcOperations.GetDashboard, null);
                case "history":
                    return await AdminAsync(IpcOperations.GetHistory, new HistoryQuery { Take = 20 });
                case "live":
                    return await AdminAsync(IpcOperations.GetLiveSessions, null);
                case "rules":
                    return await AdminAsync(IpcOperations.GetRules, null);
                case "compat":
                    return await AdminAsync(IpcOperations.GetCompatibility, null);
                case "observed":
                    return await AdminAsync(IpcOperations.GetObservedInstallers, null);
                case "settings":
                    return await AdminAsync(IpcOperations.GetSettings, null);
                case "diagnostics":
                    return await AdminAsync(IpcOperations.GetDiagnostics, null);
                case "shell":
                    return await AdminAsync(
                        IpcOperations.SetShellIntegration,
                        new SetShellIntegrationRequest
                        {
                            Enabled = args.Length > 1 &&
                                      args[1].Equals("enable", StringComparison.OrdinalIgnoreCase)
                        });
                case "plan":
                    return args.Length >= 3
                        ? await PlanAsync(args[1], args[2], args.Skip(3).ToArray())
                        : Usage();
                case "plan-msi":
                    return args.Length >= 2
                        ? await PlanAsync("msi", args[1], args.Skip(2).ToArray())
                        : Usage();
                default:
                    return Usage();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static async Task<int> PlanAsync(string kind, string target, string[] original)
    {
        var isMsi = kind.Equals("msi", StringComparison.OrdinalIgnoreCase);
        var request = new LaunchPlanRequest
        {
            LaunchKind = isMsi ? InstallerLaunchKind.Msi : InstallerLaunchKind.Exe,
            TargetPath = Path.GetFullPath(target),
            OriginalArguments = string.Join(' ', original.Select(Quote))
        };
        var result = await SendAsync(IpcNames.BrokerPipe, null, IpcOperations.PlanLaunch, request, requireAdmin: false);
        Console.WriteLine(JsonSerializer.Serialize(result, IpcJson.Options));
        return 0;
    }

    private static async Task<int> AdminAsync(string operation, object? payload)
    {
        var result = await SendAsync(IpcNames.AdminPipe, ReadToken(), operation, payload, requireAdmin: true);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<JsonElement> SendAsync(
        string pipeName,
        string? token,
        string operation,
        object? payload,
        bool requireAdmin)
    {
        if (requireAdmin && !IsAdministrator())
            throw new InvalidOperationException("此操作需要管理员权限。");

        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await pipe.ConnectAsync(timeout.Token);

        var request = new IpcRequest
        {
            Id = Guid.NewGuid().ToString("D"),
            Operation = operation,
            Token = token,
            Payload = payload is null ? null : IpcJson.ToElement(payload)
        };
        var json = JsonSerializer.Serialize(request, IpcJson.Options) + "\n";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(json), timeout.Token);
        await pipe.FlushAsync(timeout.Token);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var line = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("IPC 没有返回响应。");
        var response = JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options)
                       ?? throw new InvalidOperationException("IPC 响应为空。");
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "IPC 操作失败。");
        return response.Payload ?? default;
    }

    private static string ReadToken()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DriveRedirect",
            "ipc.token");
        if (!File.Exists(path))
            throw new FileNotFoundException("IPC 令牌不存在。", path);
        return File.ReadAllText(path).Trim();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string Quote(string value)
        => value.Contains(' ') || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static int Usage()
    {
        Console.WriteLine("""
            DriveRedirect CLI
              ping | dashboard | history | live | rules | compat | observed | settings | diagnostics
              shell enable|disable
              plan <exe|msi> <installer-path> [original arguments...]
              plan-msi <installer-path> [original arguments...]
            """);
        return 2;
    }
}

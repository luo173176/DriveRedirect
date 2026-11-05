using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;
using DriveRedirect.Infrastructure.Data;
using DriveRedirect.Infrastructure.Installers.Adapters;
using Serilog;

namespace DriveRedirect.Infrastructure.Installers;

public sealed class InstallVerificationService
{
    private readonly AppRepository _repository;
    private readonly ILogger _logger;

    public InstallVerificationService(AppRepository repository, ILogger logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task CompleteAsync(InstallationRecord record, int? exitCode, CancellationToken ct = default)
    {
        if (record.Status is not (InstallationStatus.Planned or InstallationStatus.Launching or InstallationStatus.Running))
            return;

        var now = DateTimeOffset.Now;
        var successExit = exitCode is null or 0 or 1641 or 3010;
        var cancelled = exitCode is 1602 or 1223;
        string? actualPath = null;
        string? verificationSource = null;
        string? failureReason = null;
        var status = InstallationStatus.Unverified;

        if (cancelled)
        {
            status = InstallationStatus.Cancelled;
            failureReason = "用户取消安装或安装被终止。";
        }
        else if (!successExit)
        {
            status = InstallationStatus.Failed;
            failureReason = $"安装器退出代码 {exitCode}。";
        }
        else
        {
            var target = record.TargetPath;
            if (record.WasRedirected && !string.IsNullOrWhiteSpace(target))
            {
                for (var attempt = 0; attempt < 12 && !ct.IsCancellationRequested; attempt++)
                {
                    if (DirectoryHasContent(target, record.StartedAt))
                        break;
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }

            if (record.WasRedirected && !string.IsNullOrWhiteSpace(target) && DirectoryHasContent(target, record.StartedAt))
            {
                actualPath = target;
                verificationSource = "filesystem";
                status = InstallationStatus.Redirected;
            }
            else
            {
                var registryPath = TryFindInstalledLocation(record.SoftwareName);
                if (!string.IsNullOrWhiteSpace(registryPath))
                {
                    actualPath = registryPath;
                    verificationSource = "uninstall-registry";
                }
                else if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
                {
                    actualPath = target;
                    verificationSource = "filesystem-exists";
                }
                else if (!string.IsNullOrWhiteSpace(record.TargetPath) && Directory.Exists(record.TargetPath))
                {
                    actualPath = record.TargetPath;
                    verificationSource = "planned-target-exists";
                }

                if (!string.IsNullOrWhiteSpace(actualPath))
                {
                    if (record.WasRedirected && !string.IsNullOrWhiteSpace(target) &&
                        WindowsPathTools.IsUnder(actualPath, target))
                    {
                        status = InstallationStatus.Redirected;
                    }
                    else if (record.WasRedirected && actualPath.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
                    {
                        status = InstallationStatus.Unverified;
                        failureReason = "安装器未接受自动重定向，注册表记录的安装位置仍在 C 盘。";
                    }
                    else if (record.Mode == HandlingMode.Assisted)
                    {
                        status = InstallationStatus.Assisted;
                    }
                    else
                    {
                        status = InstallationStatus.Succeeded;
                    }
                }
                else if (record.WasRedirected)
                {
                    status = InstallationStatus.Unverified;
                    failureReason = "安装器正常退出，但未在目标路径或卸载注册表中找到安装证据。";
                }
                else
                {
                    status = InstallationStatus.Succeeded;
                    failureReason = "安装器正常退出；未找到可验证的安装位置。";
                    verificationSource = "exit-code";
                }
            }
        }

        if (record.Mode == HandlingMode.Assisted && status == InstallationStatus.Succeeded)
            status = InstallationStatus.Assisted;

        record.EndedAt = now;
        record.ExitCode = exitCode;
        record.ActualPath = actualPath;
        record.VerificationSource = verificationSource;
        record.Status = status;
        record.FailureReason = failureReason ?? record.FailureReason;
        record.LastUpdatedAt = now;
        await _repository.SaveRecordAsync(record, ct);

        _logger.Information(
            "安装结果验证完成 {SessionId} {Software} status={Status} redirected={Redirected} actual={ActualPath} exit={ExitCode}",
            record.Id,
            record.SoftwareName,
            status,
            record.WasRedirected,
            actualPath,
            exitCode);
    }

    private static bool DirectoryHasContent(string path, DateTimeOffset sessionStartedAt)
    {
        try
        {
            if (!Directory.Exists(path))
                return false;
            using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            if (!enumerator.MoveNext())
                return false;
            try
            {
                var created = Directory.GetCreationTimeUtc(path);
                if (created < sessionStartedAt.UtcDateTime.AddMinutes(-3) &&
                    Directory.GetLastWriteTimeUtc(path) < sessionStartedAt.UtcDateTime.AddMinutes(-3))
                    return true; // Existing target from a prior install is still valid evidence.
            }
            catch
            {
                // Content itself is sufficient.
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryFindInstalledLocation(string softwareName)
    {
        try
        {
            return AdapterHelpers.FindExistingInstallLocation(softwareName);
        }
        catch
        {
            return null;
        }
    }
}

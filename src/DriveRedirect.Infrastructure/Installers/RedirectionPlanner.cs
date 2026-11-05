using System.Text.Json;
using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;
using DriveRedirect.Infrastructure.Configuration;
using DriveRedirect.Infrastructure.Data;
using DriveRedirect.Infrastructure.Installers.Adapters;
using DriveRedirect.Infrastructure.Installers.Detection;

namespace DriveRedirect.Infrastructure.Installers;

public sealed class PlanningResult
{
    public required LaunchDirective Directive { get; init; }
    public InstallationRecord? Record { get; init; }
    public AdapterAssessment? Assessment { get; init; }
    public InstallerArtifact? Artifact { get; init; }
}

public sealed class RedirectionPlanner
{
    private readonly InstallerDetector _detector;
    private readonly AdapterRegistry _adapters;
    private readonly AppRepository _repository;
    private readonly PathRedirectionEngine _pathEngine;
    private readonly AppPaths _paths;
    private readonly Windows.WindowsDiskProbe _diskProbe;

    public RedirectionPlanner(
        InstallerDetector detector,
        AdapterRegistry adapters,
        AppRepository repository,
        PathRedirectionEngine pathEngine,
        AppPaths paths,
        Windows.WindowsDiskProbe diskProbe)
    {
        _detector = detector;
        _adapters = adapters;
        _repository = repository;
        _pathEngine = pathEngine;
        _paths = paths;
        _diskProbe = diskProbe;
    }

    public async Task<PlanningResult> PlanLaunchAsync(
        LaunchPlanRequest request,
        string? authenticatedUserSid,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.TargetPath))
            throw new ArgumentException("安装程序路径为空。");
        var installerPath = Path.GetFullPath(request.TargetPath.Trim().Trim('"'));
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("安装程序不存在。", installerPath);

        var settings = await _repository.GetSettingsAsync(ct);
        var rules = await _repository.GetRulesAsync(ct);
        var artifact = await _detector.AnalyzeAsync(installerPath, ct);
        var softwareName = InstallerNames.InferSoftwareName(artifact.ProductName, artifact.FileDescription, artifact.FileName);
        var maintenance = InstallerNames.IsMaintenanceInvocation(request.OriginalArguments);

        if (!artifact.IsInstaller)
        {
            return new PlanningResult
            {
                Artifact = artifact,
                Directive = RawDirective(
                    Guid.Empty,
                    request,
                    artifact,
                    softwareName,
                    HandlingMode.PassThrough,
                    CompatibilityLevel.Unsupported,
                    "未识别为受支持的安装器，原样启动。",
                    null)
            };
        }

        var adapter = _adapters.Resolve(artifact);
        AdapterAssessment? assessment = null;
        string? assessmentError = null;
        if (adapter is not null)
        {
            try
            {
                assessment = await adapter.AssessAsync(artifact, new InstallerContext
                {
                    OriginalArguments = request.OriginalArguments,
                    Settings = settings,
                    Paths = _paths,
                    SessionId = Guid.NewGuid(),
                    UserSid = authenticatedUserSid,
                    IsMaintenance = maintenance
                }, ct);
            }
            catch (Exception ex)
            {
                assessmentError = ex.Message;
            }
        }

        var sessionId = Guid.NewGuid();
        var mode = HandlingMode.PassThrough;
        var compatibility = assessment?.Compatibility ?? CompatibilityLevel.Unsupported;
        var strategy = assessment?.Strategy ?? (assessmentError is null ? "没有匹配的安装器适配器" : "适配器评估失败");
        var reason = assessment?.Limitation ?? assessmentError;
        var originalPath = assessment?.OriginalInstallPath;
        string? targetPath = null;
        string? userMessage = null;
        LaunchDirective directive;
        var record = CreateBaseRecord(
            sessionId, artifact, softwareName, request, authenticatedUserSid,
            assessment, mode, compatibility, strategy);

        if (maintenance)
        {
            reason = "检测到修复、更新或卸载调用；不迁移现有软件。";
            directive = RawDirective(sessionId, request, artifact, softwareName, HandlingMode.PassThrough,
                compatibility, reason, assessment);
        }
        else if (assessment is null)
        {
            mode = HandlingMode.PassThrough;
            reason ??= "未找到适配器，安全放行。";
            directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, null);
        }
        else if (artifact.Type == InstallerType.Msix)
        {
            mode = HandlingMode.PassThrough;
            reason = assessment.Limitation ?? "MSIX/AppX 由 Windows 部署子系统管理，安全放行。";
            directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, assessment);
        }
        else if (artifact.Msi?.IsInstalled == true)
        {
            mode = HandlingMode.PassThrough;
            reason = "该 MSI ProductCode 已安装，遵循不自动迁移现有软件原则。";
            directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, assessment);
        }
        else if (!settings.AutomaticRedirectEnabled)
        {
            mode = HandlingMode.PassThrough;
            reason = "自动重定向已关闭。";
            directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, assessment);
        }
        else if (!string.IsNullOrWhiteSpace(assessment.UserSpecifiedPathReason))
        {
            mode = HandlingMode.PassThrough;
            reason = assessment.UserSpecifiedPathReason;
            directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, assessment);
        }
        else if (string.IsNullOrWhiteSpace(originalPath))
        {
            mode = HandlingMode.Assisted;
            reason = "无法确定安装器的默认安装目录。";
            userMessage = BuildAssistedMessage(softwareName, null, assessment, reason);
            directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, assessment, userMessage);
        }
        else
        {
            var userSpecified = InstallerNames.ContainsPathOverride(request.OriginalArguments, artifact.Type);
            var pathDecision = _pathEngine.Evaluate(
                softwareName,
                originalPath,
                rules,
                settings,
                userSpecified,
                artifact.SizeBytes);

            compatibility = assessment.Compatibility;
            targetPath = pathDecision.TargetPath;

            if (pathDecision.Allowed && assessment.SupportsAutomaticRedirect)
            {
                try
                {
                    var context = new InstallerContext
                    {
                        OriginalArguments = request.OriginalArguments,
                        Settings = settings,
                        Paths = _paths,
                        SessionId = sessionId,
                        UserSid = authenticatedUserSid,
                        IsMaintenance = maintenance,
                        HasUserSpecifiedPath = userSpecified
                    };
                    var launch = await adapter!.BuildLaunchAsync(artifact, assessment, pathDecision.TargetPath!, context, ct);
                    mode = HandlingMode.Automatic;
                    targetPath = launch.VerificationPath ?? pathDecision.TargetPath;
                    strategy = launch.StrategyDetail ?? assessment.Strategy;
                    directive = new LaunchDirective
                    {
                        SessionId = sessionId,
                        Redirected = true,
                        Mode = mode,
                        FileName = launch.FileName,
                        Arguments = launch.Arguments,
                        WorkingDirectory = launch.WorkingDirectory,
                        InstallerType = artifact.Type,
                        SoftwareName = softwareName,
                        InstallerPath = artifact.FilePath,
                        OriginalInstallPath = pathDecision.OriginalPath,
                        TargetInstallPath = targetPath,
                        VerificationPath = targetPath,
                        Reason = pathDecision.Reason,
                        AdapterId = assessment.AdapterId,
                        AdapterVersion = assessment.AdapterVersion,
                        Strategy = strategy,
                        Compatibility = assessment.Compatibility,
                        LogPath = launch.LogPath
                    };
                    record.Mode = mode;
                    record.TargetPath = targetPath;
                    record.OriginalPath = pathDecision.OriginalPath;
                    record.WasRedirected = true;
                    record.Strategy = strategy;
                    record.LaunchArguments = launch.Arguments;
                    record.MsiLogPath = launch.LogPath;
                }
                catch (Exception ex)
                {
                    mode = HandlingMode.PassThrough;
                    reason = "自动重定向准备失败，已安全放行：" + ex.Message;
                    userMessage = null;
                    directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, assessment);
                    record.FailureReason = reason;
                }
            }
            else if (assessment.SupportsAutomaticRedirect)
            {
                mode = IsDiskOrSpaceFailure(pathDecision.Reason) ? HandlingMode.Assisted : HandlingMode.PassThrough;
                reason = pathDecision.Reason;
                if (pathDecision.Allowed)
                {
                    mode = HandlingMode.Assisted;
                    reason = "适配器要求辅助模式。";
                }
                userMessage = mode == HandlingMode.Assisted
                    ? BuildAssistedMessage(softwareName, targetPath, assessment, reason)
                    : null;
                directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, assessment, userMessage);
            }
            else
            {
                mode = HandlingMode.Assisted;
                reason = assessment.Limitation ?? pathDecision.Reason;
                userMessage = BuildAssistedMessage(softwareName, targetPath, assessment, reason);
                directive = RawDirective(sessionId, request, artifact, softwareName, mode, compatibility, reason, assessment, userMessage);
            }

            record.OriginalPath = record.OriginalPath ?? pathDecision.OriginalPath;
            record.TargetPath = record.TargetPath ?? targetPath;
            record.FailureReason = mode is HandlingMode.Assisted or HandlingMode.PassThrough
                ? reason
                : record.FailureReason;
        }

        if (!maintenance && (assessment is not null || artifact.Type != InstallerType.Unknown))
        {
            record.Mode = directive.Mode;
            record.OriginalPath ??= directive.OriginalInstallPath;
            record.TargetPath ??= directive.TargetInstallPath;
            record.Compatibility = directive.Compatibility;
            record.AdapterId = directive.AdapterId ?? assessment?.AdapterId;
            record.AdapterVersion = directive.AdapterVersion ?? assessment?.AdapterVersion;
            record.Strategy = directive.Strategy ?? strategy;
            record.LaunchArguments = directive.Arguments;
            record.MsiLogPath ??= directive.LogPath;
            record.WasRedirected = directive.Redirected;
            record.Status = InstallationStatus.Planned;
            record.LastUpdatedAt = DateTimeOffset.Now;
            await _repository.SaveRecordAsync(record, ct);
            await _repository.SaveObservationAsync(new ObservedInstaller
            {
                Id = Guid.NewGuid(),
                DetectedAt = DateTimeOffset.Now,
                InstallerPath = artifact.FilePath,
                InstallerType = artifact.Type,
                SoftwareName = softwareName,
                AdapterId = assessment?.AdapterId,
                Compatibility = directive.Compatibility,
                Mode = directive.Mode,
                Reason = directive.Reason,
                Hash = artifact.Sha256
            }, artifact, ct);
        }
        else if (maintenance)
        {
            record.Mode = HandlingMode.PassThrough;
            record.Status = InstallationStatus.Planned;
            record.Compatibility = compatibility;
            record.OriginalPath = originalPath;
            record.FailureReason = reason;
            record.LastUpdatedAt = DateTimeOffset.Now;
            await _repository.SaveRecordAsync(record, ct);
        }

        return new PlanningResult
        {
            Directive = directive,
            Record = record,
            Assessment = assessment,
            Artifact = artifact
        };
    }

    private static InstallationRecord CreateBaseRecord(
        Guid sessionId,
        InstallerArtifact artifact,
        string softwareName,
        LaunchPlanRequest request,
        string? userSid,
        AdapterAssessment? assessment,
        HandlingMode mode,
        CompatibilityLevel compatibility,
        string strategy)
        => new()
        {
            Id = sessionId,
            StartedAt = DateTimeOffset.Now,
            InstallerType = artifact.Type,
            SoftwareName = softwareName,
            InstallerPath = artifact.FilePath,
            InstallerVersion = artifact.ProductVersion,
            InstallerHash = artifact.Sha256,
            Mode = mode,
            Compatibility = compatibility,
            Status = InstallationStatus.Planned,
            AdapterId = assessment?.AdapterId,
            AdapterVersion = assessment?.AdapterVersion,
            Strategy = strategy,
            OriginalPath = assessment?.OriginalInstallPath,
            UserSid = userSid,
            MetadataJson = JsonSerializer.Serialize(new
            {
                artifact.ProductName,
                artifact.CompanyName,
                artifact.ProductVersion,
                artifact.SizeBytes,
                artifact.DetectionSignals,
                request.OriginalArguments
            })
        };

    private static LaunchDirective RawDirective(
        Guid sessionId,
        LaunchPlanRequest request,
        InstallerArtifact artifact,
        string softwareName,
        HandlingMode mode,
        CompatibilityLevel compatibility,
        string reason,
        AdapterAssessment? assessment,
        string? userMessage = null)
    {
        var fileName = artifact.FilePath;
        var arguments = request.OriginalArguments.Trim();
        var workingDirectory = Path.GetDirectoryName(artifact.FilePath) ?? Environment.CurrentDirectory;

        if (artifact.Type is InstallerType.WindowsMsi or InstallerType.Wix)
        {
            fileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            arguments = $"/i \"{artifact.FilePath}\"" +
                        (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments);
            workingDirectory = Environment.SystemDirectory;
        }

        return new LaunchDirective
        {
            SessionId = sessionId,
            Redirected = false,
            Mode = mode,
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            InstallerType = artifact.Type,
            SoftwareName = softwareName,
            InstallerPath = artifact.FilePath,
            OriginalInstallPath = assessment?.OriginalInstallPath,
            TargetInstallPath = null,
            VerificationPath = assessment?.OriginalInstallPath,
            Reason = reason,
            UserMessage = userMessage,
            AdapterId = assessment?.AdapterId,
            AdapterVersion = assessment?.AdapterVersion,
            Strategy = assessment?.Strategy,
            AssistedGuidance = assessment?.AssistedGuidance,
            Compatibility = compatibility
        };
    }

    private static string BuildAssistedMessage(
        string softwareName,
        string? targetPath,
        AdapterAssessment assessment,
        string reason)
    {
        var target = targetPath ?? assessment.AssistedGuidance ?? "D 盘对应目录";
        return $"DriveRedirect 已识别「{softwareName}」，但此安装器无法安全地自动改写默认路径。\n\n" +
               $"建议安装位置：\n{target}\n\n" +
               $"原因：{reason}\n\n" +
               "安装器将按原有设置启动。请在安装界面中手动选择上述 D 盘路径，以保留更新和卸载能力。";
    }

    private static bool IsDiskOrSpaceFailure(string reason)
        => reason.Contains("盘不存在", StringComparison.Ordinal) ||
           reason.Contains("盘不可写", StringComparison.Ordinal) ||
           reason.Contains("剩余空间不足", StringComparison.Ordinal) ||
           reason.Contains("未就绪", StringComparison.Ordinal) ||
           reason.Contains("不是本地固定磁盘", StringComparison.Ordinal);
}

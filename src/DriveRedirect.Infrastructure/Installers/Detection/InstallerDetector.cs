using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;

namespace DriveRedirect.Infrastructure.Installers.Detection;

public sealed class InstallerDetector
{
    private readonly MsiNativeReader _msiReader;
    private static readonly string[] GameLauncherNames =
    [
        "steamsetup", "steam", "epicgameslauncher", "epicinstaller", "battle.net", "battlenet",
        "riotclientservices", "riot_client", "eaappinstaller", "origin", "ubisoftconnect", "uplay",
        "goggalaxy", "xboxinstaller", "wegame", "gameloop", "tencentgameloop"
    ];

    public InstallerDetector(MsiNativeReader msiReader)
    {
        _msiReader = msiReader;
    }

    public async Task<InstallerArtifact> AnalyzeAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("安装包不存在。", fullPath);

        var fileInfo = new FileInfo(fullPath);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("不支持网络路径中的安装包。");

        var extension = fileInfo.Extension.ToLowerInvariant();
        var versionInfo = TryGetVersionInfo(fullPath);
        var signals = new List<string>();
        var type = InstallerType.Unknown;
        MsiPackageInfo? msiInfo = null;
        IReadOnlyDictionary<string, bool>? markers = null;

        if (extension == ".msi")
        {
            type = InstallerType.WindowsMsi;
            signals.Add("MSI 数据库");
            try
            {
                msiInfo = await Task.Run(() => _msiReader.Read(fullPath), ct);
                if (!string.IsNullOrWhiteSpace(msiInfo.ProductName))
                    signals.Add("MSI ProductName=" + msiInfo.ProductName);
                markers = await ScanMarkersAsync(fullPath, ct);
                if (markers.GetValueOrDefault("Wix"))
                {
                    type = InstallerType.Wix;
                    signals.Add("WiX MSI 标记");
                }
            }
            catch (Exception ex)
            {
                signals.Add("MSI 元数据读取失败：" + ex.Message);
            }
        }
        else if (extension is ".msix" or ".msixbundle" or ".appx" or ".appxbundle")
        {
            type = InstallerType.Msix;
            signals.Add("MSIX/AppX 系统部署包");
        }
        else if (extension == ".exe")
        {
            markers = await ScanMarkersAsync(fullPath, ct);
            var fileName = Path.GetFileNameWithoutExtension(fullPath);
            if (GameLauncherNames.Any(n => fileName.Contains(n, StringComparison.OrdinalIgnoreCase) ||
                                           (versionInfo?.CompanyName?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                type = InstallerType.GameLauncher;
                signals.Add("已知游戏启动器名称");
            }
            else if (markers.GetValueOrDefault("Inno"))
            {
                type = InstallerType.InnoSetup;
                signals.Add("Inno Setup 数据标记");
            }
            else if (markers.GetValueOrDefault("Nsis"))
            {
                type = InstallerType.Nsis;
                signals.Add("Nullsoft Install System 标记");
            }
            else if (markers.GetValueOrDefault("InstallShield"))
            {
                type = InstallerType.InstallShield;
                signals.Add("InstallShield 标记");
                if (markers.GetValueOrDefault("InstallShieldBasicMsi"))
                    signals.Add("InstallShield Basic MSI 标记");
            }
            else if (markers.GetValueOrDefault("WixBundle"))
            {
                type = InstallerType.CustomExe;
                signals.Add("WiX Burn Bundle；需 bundle 专用适配");
            }
            else if (LooksLikeInstallerByMetadata(fileName, versionInfo))
            {
                type = InstallerType.CustomExe;
                signals.Add("文件名或版本元数据表明这是安装器");
            }
            else
            {
                type = InstallerType.Unknown;
                signals.Add("未发现受支持的安装器框架标记");
            }
        }

        string? hash = null;
        if (fileInfo.Length <= 512L * 1024 * 1024)
        {
            try
            {
                hash = await ComputeSha256Async(fullPath, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                signals.Add("哈希计算失败：" + ex.Message);
            }
        }

        var productName = msiInfo?.ProductName ?? versionInfo?.ProductName;
        var companyName = msiInfo?.Manufacturer ?? versionInfo?.CompanyName;
        var productVersion = msiInfo?.ProductVersion ?? versionInfo?.ProductVersion;

        if (type == InstallerType.Msix)
        {
            productName = InstallerNames.InferSoftwareName(productName, versionInfo?.FileDescription, fileInfo.Name);
            companyName = versionInfo?.CompanyName;
            productVersion = versionInfo?.ProductVersion;
        }

        return new InstallerArtifact
        {
            FilePath = fullPath,
            Type = type,
            FileName = fileInfo.Name,
            SizeBytes = fileInfo.Length,
            ProductName = productName,
            CompanyName = companyName,
            ProductVersion = productVersion,
            FileDescription = versionInfo?.FileDescription,
            Sha256 = hash,
            Msi = msiInfo,
            DetectionSignals = signals
        };
    }

    public async Task<InstallerArtifact> AnalyzeForRecordingAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await AnalyzeAsync(path, ct);
        }
        catch (Exception ex)
        {
            var info = new FileInfo(path);
            return new InstallerArtifact
            {
                FilePath = Path.GetFullPath(path),
                Type = InstallerType.Unknown,
                FileName = info.Name,
                SizeBytes = info.Exists ? info.Length : 0,
                DetectionSignals = ["分析失败：" + ex.Message]
            };
        }
    }

    private static FileVersionInfo? TryGetVersionInfo(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeInstallerByMetadata(string fileName, FileVersionInfo? info)
    {
        string[] hints = ["setup", "install", "installer", "bootstrap", "webinstall"];
        if (hints.Any(h => fileName.Contains(h, StringComparison.OrdinalIgnoreCase)))
            return true;
        var metadata = string.Join(' ', info?.ProductName, info?.FileDescription, info?.OriginalFilename);
        return hints.Any(h => metadata.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyDictionary<string, bool>> ScanMarkersAsync(string path, CancellationToken ct)
    {
        var patterns = new Dictionary<string, string[]>
        {
            ["Inno"] = ["Inno Setup Setup Data", "Inno Setup"],
            ["Nsis"] = ["NullsoftInst", "Nullsoft Install System"],
            ["InstallShield"] = ["InstallShield", "ISSetup.dll", "InstallShield Wizard"],
            ["InstallShieldBasicMsi"] = ["Basic MSI", "ISBasicMsi", "InstallShield MSI"],
            ["Wix"] = ["WixToolset", "WiX Toolset", "WixUI"],
            ["WixBundle"] = ["WixBundle", "BurnStub"]
        };
        var result = patterns.Keys.ToDictionary(k => k, _ => false);

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            var overlapMax = 64;
            var carry = new byte[overlapMax];
            var carryLength = 0;
            var remaining = stream.Length;
            var started = Stopwatch.StartNew();

            while (remaining > 0 && !result.Values.All(v => v))
            {
                ct.ThrowIfCancellationRequested();
                if (started.Elapsed > TimeSpan.FromSeconds(4))
                    break;

                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read <= 0)
                    break;
                remaining -= read;

                var data = new byte[carryLength + read];
                Buffer.BlockCopy(carry, 0, data, 0, carryLength);
                Buffer.BlockCopy(buffer, 0, data, carryLength, read);

                foreach (var (name, values) in patterns)
                {
                    if (result[name])
                        continue;
                    if (values.Any(v => ContainsAsciiOrUtf16(data, v)))
                        result[name] = true;
                }

                carryLength = Math.Min(overlapMax, data.Length);
                Buffer.BlockCopy(data, data.Length - carryLength, carry, 0, carryLength);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Metadata analysis continues with the signals already discovered.
        }

        return result;
    }

    private static bool ContainsAsciiOrUtf16(ReadOnlySpan<byte> data, string text)
    {
        return data.IndexOf(Encoding.ASCII.GetBytes(text)) >= 0 ||
               data.IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

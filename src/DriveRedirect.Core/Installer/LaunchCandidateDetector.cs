using System.Diagnostics;

namespace DriveRedirect.Core.Installer;

public static class LaunchCandidateDetector
{
    private static readonly string[] InstallerNameHints =
    [
        "setup", "install", "installer", "bootstrap", "webinstall"
    ];

    private static readonly string[] MaintenanceNameHints =
    [
        "uninstall", "unins", "update"
    ];

    public static bool IsCandidate(string path, out string reason)
    {
        reason = "";
        try
        {
            if (!File.Exists(path))
            {
                reason = "文件不存在。";
                return false;
            }

            var extension = Path.GetExtension(path);
            if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".msix", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".appx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase))
            {
                reason = "安装包扩展名。";
                return true;
            }

            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                reason = "不是可执行安装包。";
                return false;
            }

            if (Rules.WindowsPathTools.IsSystemProtectedPath(path))
            {
                reason = "系统保护目录中的可执行文件不作为安装器拦截。";
                return false;
            }

            var fileName = Path.GetFileNameWithoutExtension(path);
            if (MaintenanceNameHints.Any(hint => fileName.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            {
                reason = "文件名表明这是更新或卸载程序，按维护调用放行。";
                return false;
            }

            if (InstallerNameHints.Any(hint => fileName.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            {
                reason = "文件名包含安装器特征。";
                return true;
            }

            var info = FileVersionInfoSafe(path);
            if (info is not null)
            {
                var metadata = string.Join(' ', info.ProductName, info.FileDescription, info.OriginalFilename);
                if (MaintenanceNameHints.Any(hint => metadata.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                {
                    reason = "版本元数据表明这是更新或卸载程序，按维护调用放行。";
                    return false;
                }
                if (InstallerNameHints.Any(hint => metadata.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                {
                    reason = "版本元数据包含安装器特征。";
                    return true;
                }
            }

            if (HasInstallerMarkerNearEdges(path))
            {
                reason = "文件包含安装器框架标记。";
                return true;
            }

            reason = "未发现安装器特征。";
            return false;
        }
        catch (Exception ex)
        {
            reason = "启动候选检测失败：" + ex.Message;
            return false;
        }
    }

    private static FileVersionInfo? FileVersionInfoSafe(string path)
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

    private static bool HasInstallerMarkerNearEdges(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.RandomAccess);
            var buffer = new byte[256 * 1024];
            var head = stream.Read(buffer);
            if (ContainsAsciiOrUtf16(buffer.AsSpan(0, head), "Inno Setup Setup Data") ||
                ContainsAsciiOrUtf16(buffer.AsSpan(0, head), "NullsoftInst") ||
                ContainsAsciiOrUtf16(buffer.AsSpan(0, head), "InstallShield") ||
                ContainsAsciiOrUtf16(buffer.AsSpan(0, head), "WixBundle"))
                return true;

            if (stream.Length > buffer.Length)
            {
                stream.Seek(Math.Max(0, stream.Length - buffer.Length), SeekOrigin.Begin);
                var tail = stream.Read(buffer);
                return ContainsAsciiOrUtf16(buffer.AsSpan(0, tail), "Inno Setup Setup Data") ||
                       ContainsAsciiOrUtf16(buffer.AsSpan(0, tail), "NullsoftInst") ||
                       ContainsAsciiOrUtf16(buffer.AsSpan(0, tail), "InstallShield") ||
                       ContainsAsciiOrUtf16(buffer.AsSpan(0, tail), "WixBundle");
            }
        }
        catch
        {
            // Candidate probing must never block a launch.
        }
        return false;
    }

    private static bool ContainsAsciiOrUtf16(ReadOnlySpan<byte> data, string text)
    {
        return data.IndexOf(System.Text.Encoding.ASCII.GetBytes(text)) >= 0 ||
               data.IndexOf(System.Text.Encoding.Unicode.GetBytes(text)) >= 0;
    }
}

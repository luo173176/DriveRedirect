using System.Collections.Concurrent;
using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;

namespace DriveRedirect.Infrastructure.Windows;

public sealed class WindowsDiskProbe : IDiskProbe
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, DiskProbeResult Result)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _cacheDuration;

    public WindowsDiskProbe(TimeSpan? cacheDuration = null)
    {
        _cacheDuration = cacheDuration ?? TimeSpan.FromSeconds(5);
    }

    public DiskProbeResult Probe(string rootOrDrive)
    {
        string root;
        try
        {
            var drive = WindowsPathTools.NormalizeDriveRoot(rootOrDrive);
            root = drive;
        }
        catch (Exception ex)
        {
            return new DiskProbeResult
            {
                Root = rootOrDrive,
                Exists = false,
                Error = ex.Message
            };
        }

        if (_cache.TryGetValue(root, out var cached) && DateTimeOffset.Now - cached.At < _cacheDuration)
            return cached.Result;

        var result = ProbeCore(root);
        _cache[root] = (DateTimeOffset.Now, result);
        return result;
    }

    public DiskInfo GetDiskInfo(string rootOrDrive)
    {
        var result = Probe(rootOrDrive);
        return new DiskInfo
        {
            Name = result.Root.TrimEnd('\\'),
            Root = result.Root,
            TotalBytes = result.TotalBytes,
            FreeBytes = result.FreeBytes,
            IsReady = result.IsReady,
            Error = result.Error
        };
    }

    private static DiskProbeResult ProbeCore(string root)
    {
        try
        {
            var info = new DriveInfo(root);
            if (!info.IsReady)
            {
                return new DiskProbeResult
                {
                    Root = root,
                    Exists = true,
                    IsReady = false,
                    IsFixed = info.DriveType == DriveType.Fixed,
                    Error = "磁盘未就绪。"
                };
            }

            var writable = false;
            string? writeError = null;
            try
            {
                var probePath = Path.Combine(root, $".driveredirect-write-{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                    stream.WriteByte(0);
                }
                writable = true;
            }
            catch (Exception ex)
            {
                writeError = ex.Message;
            }

            return new DiskProbeResult
            {
                Root = root,
                Exists = true,
                IsReady = info.IsReady,
                IsWritable = writable,
                IsFixed = info.DriveType == DriveType.Fixed,
                TotalBytes = info.TotalSize,
                FreeBytes = info.AvailableFreeSpace,
                FileSystem = info.DriveFormat,
                Error = writeError
            };
        }
        catch (Exception ex)
        {
            return new DiskProbeResult
            {
                Root = root,
                Exists = false,
                Error = ex.Message
            };
        }
    }
}

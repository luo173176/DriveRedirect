using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;

namespace DriveRedirect.Infrastructure.Installers.Detection;

public sealed partial class MsiTransformBuilder
{
    public Task<string> BuildAsync(
        string msiPath,
        string targetPath,
        string directoryProperty,
        string outputDirectory,
        Guid sessionId,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            var info = new MsiNativeReader().Read(msiPath);
            return BuildCore(msiPath, targetPath, directoryProperty, info, outputDirectory, sessionId);
        }, ct);

    public Task<string> BuildAsync(
        string msiPath,
        string targetPath,
        string directoryProperty,
        MsiPackageInfo package,
        string outputDirectory,
        Guid sessionId,
        CancellationToken ct = default)
        => Task.Run(
            () => BuildCore(msiPath, targetPath, directoryProperty, package, outputDirectory, sessionId),
            ct);

    private static string BuildCore(
        string msiPath,
        string targetPath,
        string directoryProperty,
        MsiPackageInfo package,
        string outputDirectory,
        Guid sessionId)
    {
        if (!DirectoryIdRegex().IsMatch(directoryProperty))
            throw new InvalidOperationException("MSI 目录属性格式无效。");
        if (!Path.IsPathFullyQualified(targetPath))
            throw new InvalidOperationException("MSI 目标目录必须是绝对路径。");
        var targetRoot = Path.GetPathRoot(targetPath);
        if (!string.Equals(targetRoot, @"D:\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MSI Transform 只允许把目录重定向到 D 盘。");

        var originalPath = package.DefaultInstallPath;
        var chain = package.InstallDirectoryChain;
        var baseNode = chain.LastOrDefault(n => n.IsBaseFolder);
        var anchorNode = baseNode is null
            ? null
            : chain.SkipWhile(n => !string.Equals(n.DirectoryId, baseNode.DirectoryId, StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
        var selectedNode = chain.LastOrDefault();
        if (string.IsNullOrWhiteSpace(originalPath) ||
            baseNode is null ||
            anchorNode is null ||
            selectedNode is null ||
            string.IsNullOrWhiteSpace(anchorNode.Path))
        {
            throw new InvalidOperationException("MSI 私有目录链无法解析，拒绝生成未经验证的 Transform。");
        }

        var originalAnchor = WindowsPathTools.NormalizeDirectory(anchorNode.Path);
        var originalSelected = WindowsPathTools.NormalizeDirectory(originalPath);
        if (!WindowsPathTools.IsSamePath(originalSelected, selectedNode.Path ?? "") &&
            !WindowsPathTools.IsUnder(originalSelected, originalAnchor))
        {
            throw new InvalidOperationException("MSI 安装目录与目录表锚点不一致，拒绝改写。");
        }

        var suffix = originalSelected.Length > originalAnchor.Length
            ? originalSelected[originalAnchor.Length..].TrimStart('\\')
            : "";
        var targetSelected = WindowsPathTools.NormalizeDirectory(targetPath);
        var targetAnchor = targetSelected;
        if (suffix.Length > 0)
        {
            var expectedSuffix = "\\" + suffix;
            if (!targetSelected.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标路径没有保留原始子目录层级，拒绝改写 MSI 目录表。");
            targetAnchor = targetSelected[..^expectedSuffix.Length];
        }

        var segments = targetAnchor.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[0].EndsWith(':'))
            throw new InvalidOperationException("MSI 目标目录层级无效。");
        var anchorLeaf = segments[^1];
        var intermediate = segments[1..^1];
        if (!IsValidDirectorySegment(anchorLeaf) || intermediate.Any(s => !IsValidDirectorySegment(s)))
            throw new InvalidOperationException("MSI 目标目录包含不允许作为 Directory 名称的字符。");

        Directory.CreateDirectory(outputDirectory);
        var transformPath = Path.Combine(outputDirectory, $"{sessionId:D}.mst");
        var tempMsi = Path.Combine(Path.GetTempPath(), $"driveredirect-{Guid.NewGuid():N}.msi");
        File.Copy(msiPath, tempMsi, true);

        try
        {
            ModifyDirectoryTable(
                tempMsi,
                anchorNode.DirectoryId,
                anchorLeaf,
                intermediate,
                sessionId);

            IntPtr referenceDb = IntPtr.Zero;
            IntPtr modifiedDb = IntPtr.Zero;
            try
            {
                var result = MsiTransformNative.MsiOpenDatabase(msiPath, IntPtr.Zero, out referenceDb);
                if (result != 0)
                    throw new MsiPackageReadException("无法打开原始 MSI 以生成 Transform", result);

                result = MsiTransformNative.MsiOpenDatabase(tempMsi, IntPtr.Zero, out modifiedDb);
                if (result != 0)
                    throw new MsiPackageReadException("无法打开修改后的 MSI", result);

                if (File.Exists(transformPath))
                    File.Delete(transformPath);

                result = MsiTransformNative.MsiDatabaseGenerateTransform(
                    modifiedDb,
                    referenceDb,
                    transformPath,
                    0,
                    0);
                if (result != 0)
                    throw new MsiPackageReadException("生成 MST 失败", result);

                result = MsiTransformNative.MsiCreateTransformSummaryInfo(
                    modifiedDb,
                    referenceDb,
                    transformPath,
                    0,
                    0);
                if (result != 0 && !File.Exists(transformPath))
                    throw new MsiPackageReadException("创建 MST 摘要失败", result);
            }
            finally
            {
                if (modifiedDb != IntPtr.Zero)
                    MsiTransformNative.MsiCloseHandle(modifiedDb);
                if (referenceDb != IntPtr.Zero)
                    MsiTransformNative.MsiCloseHandle(referenceDb);
            }

            if (!File.Exists(transformPath) || new FileInfo(transformPath).Length == 0)
                throw new InvalidOperationException("MSI Transform 未生成有效文件。");

            return transformPath;
        }
        finally
        {
            try
            {
                File.Delete(tempMsi);
            }
            catch
            {
                // Cleanup is best effort.
            }
        }
    }

    private static void ModifyDirectoryTable(
        string tempMsi,
        string anchorDirectoryId,
        string anchorLeaf,
        IReadOnlyList<string> intermediate,
        Guid sessionId)
    {
        var openResult = MsiTransformNative.MsiOpenDatabase(tempMsi, new IntPtr(1), out var database);
        if (openResult != 0)
            throw new MsiPackageReadException("无法以事务模式打开 MSI", openResult);

        try
        {
            var suffix = sessionId.ToString("N")[..8].ToUpperInvariant();
            var parent = "TARGETDIR";
            for (var i = 0; i < intermediate.Count; i++)
            {
                var id = $"DRDIR_{suffix}_{i}";
                Execute(
                    database,
                    $"INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) " +
                    $"VALUES ('{Escape(id)}', '{Escape(parent)}', '{Escape(intermediate[i])}')");
                parent = id;
            }

            Execute(
                database,
                $"UPDATE `Directory` SET `Directory_Parent` = '{Escape(parent)}', " +
                $"`DefaultDir` = '{Escape(anchorLeaf)}' WHERE `Directory` = '{Escape(anchorDirectoryId)}'");

            var result = MsiTransformNative.MsiDatabaseCommit(database);
            if (result != 0)
                throw new MsiPackageReadException("无法提交 MSI 目录更新", result);
        }
        finally
        {
            MsiTransformNative.MsiCloseHandle(database);
        }
    }

    private static void Execute(IntPtr database, string sql)
    {
        var result = MsiTransformNative.MsiDatabaseOpenView(database, sql, out var view);
        if (result != 0)
            throw new MsiPackageReadException("无法创建 MSI 目录更新查询", result);
        try
        {
            result = MsiTransformNative.MsiViewExecute(view, IntPtr.Zero);
            if (result != 0)
                throw new MsiPackageReadException("无法执行 MSI 目录更新", result);
        }
        finally
        {
            MsiTransformNative.MsiCloseHandle(view);
        }
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static bool IsValidDirectorySegment(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           value is not "." and not ".." &&
           value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           !value.Contains('\\') &&
           !value.Contains('/') &&
           !value.Contains(':') &&
           !value.Contains('|');

    [GeneratedRegex("^[A-Za-z0-9_.]{1,72}$")]
    private static partial Regex DirectoryIdRegex();

    private static class MsiTransformNative
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr database);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiViewExecute(IntPtr view, IntPtr record);

        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiDatabaseCommit(IntPtr database);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiDatabaseGenerateTransform(
            IntPtr database,
            IntPtr referenceDatabase,
            string transformFile,
            int reserved,
            int reserved2);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiCreateTransformSummaryInfo(
            IntPtr database,
            IntPtr referenceDatabase,
            string transformFile,
            int errorConditions,
            int validation);

        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiCloseHandle(IntPtr handle);
    }
}

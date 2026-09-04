using System.Security.Cryptography;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Infrastructure.Filesystem;

public sealed class LocalPhysicalPathResolver : ILocalPhysicalPathResolver
{
    public Task<ResolvedPhysicalPath> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // 绑定对象本身不能通过解析消除链接身份，否则 Source/External 的 no-follow 检查会被绕过。
        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("绑定对象本身是链接或重解析点，请选择真实目录或文件。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var canonical = ResolvePhysicalPath(fullPath, 0, cancellationToken);
        var key = canonical.Replace('\\', '/').Normalize();
        var lexicalKey = fullPath.Replace('\\', '/').Normalize();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) key = key.ToUpperInvariant();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) lexicalKey = lexicalKey.ToUpperInvariant();
        return Task.FromResult(new ResolvedPhysicalPath(canonical, key, lexicalKey));
    }

    private static string ResolvePhysicalPath(string fullPath, int depth, CancellationToken cancellationToken)
    {
        // 按分量解析，避免父目录别名使源与输出看似分离；未创建的尾部仍保留给 readiness 判断。
        if (depth > 40) throw new IOException("目录链接层级过深或存在循环，无法安全解析本机路径。");
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            var target = entry.ResolveLinkTarget(false)
                ?? throw new IOException("无法解析本机路径中的链接或重解析点。");
            current = ResolvePhysicalPath(Path.GetFullPath(target.FullName), depth + 1, cancellationToken);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new IOException("本机路径中的链接目标不存在，无法安全绑定。");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}

public sealed class CurrentArtifactRecoveryProbe : ICurrentArtifactRecoveryProbe
{
    public async Task<Sha256Digest?> ObserveIntegrityAsync(OutputRootLocalBinding currentRoot, RelativeStoragePath relativePath, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(currentRoot.CanonicalPath);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, combined);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new LocalStateCorruptionException("Current relative path escapes CurrentRoot.");
        if (!File.Exists(combined)) return null;
        await using var stream = new FileStream(combined, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Digest(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
    }
}

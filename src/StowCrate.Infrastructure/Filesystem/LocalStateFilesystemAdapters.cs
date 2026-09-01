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
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var key = canonical.Replace('\\', '/').Normalize();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) key = key.ToUpperInvariant();
        return Task.FromResult(new ResolvedPhysicalPath(canonical, key));
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

using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Filesystem;

public sealed partial class StorageRelocationPhysicalStore : IStorageRelocationTargetDurabilityProbe
{
    public async Task VerifyTargetDurabilityAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await VerifyUnoccupiedTargetsAsync(manifest, cancellationToken).ConfigureAwait(false);
        var before = CaptureDurabilityDirectories(manifest, cancellationToken);
        foreach (var directory in before)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!before.SequenceEqual(CaptureDurabilityDirectories(manifest, cancellationToken)))
                throw new IOException("Relocation target directories changed before durability inspection.");
            RequireIdentity(directory.Path, true, directory.Identity);
            var proof = await durability.FlushDirectoryMetadataAsync(directory.Path, cancellationToken).ConfigureAwait(false);
            if (!proof.BarrierCompleted) throw new StorageRelocationDurabilityUnavailableException();
            RequireIdentity(directory.Path, true, directory.Identity);
        }
        // 屏障 I/O 期间出现的新父目录也必须重新接受检查，不能沿用此前的缺失观察。
        var after = CaptureDurabilityDirectories(manifest, cancellationToken);
        if (!before.SequenceEqual(after)) throw new IOException("Relocation target directories changed during durability inspection.");
        await VerifyUnoccupiedTargetsAsync(manifest, cancellationToken).ConfigureAwait(false);
    }

    private static List<(string Path, StorageObjectIdentity Identity)> CaptureDurabilityDirectories(
        StorageRelocationManifest manifest, CancellationToken cancellationToken)
    {
        var result = new List<(string Path, StorageObjectIdentity Identity)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in manifest.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
            Add(root.NewRoot.CanonicalPath, root.NewIdentity);
            foreach (var entry in manifest.Entries.Where(x => x.RootKind == root.Kind))
            {
                var path = root.NewRoot.CanonicalPath;
                // final 与确定性 temp 为同级文件；必须覆盖整个现存父链，包括嵌套挂载点。
                var parts = entry.RelativePath.Value.Split('/');
                foreach (var part in parts.Take(parts.Length - 1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    path = Path.Combine(path, part);
                    if (!Exists(path)) break;
                    Add(path, InspectIdentity(path, true));
                }
            }
            RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
        }
        return result;

        void Add(string path, StorageObjectIdentity identity)
        {
            if (seen.Add(path)) result.Add((path, identity));
        }
    }
}

using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Filesystem;

public sealed partial class StorageRelocationPhysicalStore : IStorageRelocationTargetNamespaceProbe
{
    public Task VerifyUnoccupiedTargetsAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        VerifyUnoccupiedTargets(manifest.Roots, manifest.Entries, cancellationToken);
        return Task.CompletedTask;
    }

    public Task VerifyUnoccupiedTargetsAsync(StorageRelocationPhysicalInventory observation, Guid transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (transactionId == Guid.Empty) throw new ArgumentException("Transaction identity is required.", nameof(transactionId));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInventory(observation.Inventory);
        if (observation.Roots.IsDefault || observation.Entries.IsDefault
            || !observation.Roots.Select(x => new StorageRelocationRootPaths(x.Kind, x.OldRoot, x.NewRoot)).SequenceEqual(observation.Inventory.Roots)
            || !observation.Entries.Select(x => x.Placement).SequenceEqual(observation.Inventory.Entries))
            throw new ArgumentException("Physical observation does not cover the inventory.", nameof(observation));
        var entries = observation.Entries.Select(x => new StorageRelocationEntry(x.Placement.UnitId, x.Placement.RootKind,
            x.Placement.Artifact, x.Placement.RelativePath,
            StorageRelocationTempLayout.Create(transactionId, x.Placement.Artifact.VersionId, x.Placement.RelativePath), x.Identity)).ToArray();
        // 同时覆盖 final 与 temp 的字面父子冲突；真实文件系统比较语义仍需独立门槛。
        foreach (var group in entries.GroupBy(x => x.RootKind))
        {
            var paths = group.SelectMany(x => new[] { x.RelativePath.Value, x.TempRelativePath.Value }).ToArray();
            for (var i = 0; i < paths.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var j = i + 1; j < paths.Length; j++)
                    if (paths[i] == paths[j] || paths[i].StartsWith(paths[j] + "/", StringComparison.Ordinal)
                        || paths[j].StartsWith(paths[i] + "/", StringComparison.Ordinal))
                        throw new IOException("Relocation target and temporary paths collide.");
            }
        }
        VerifyUnoccupiedTargets(observation.Roots, entries, cancellationToken);
        return Task.CompletedTask;
    }

    private static void VerifyUnoccupiedTargets(IEnumerable<StorageRelocationRoot> roots,
        IEnumerable<StorageRelocationEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var root in roots)
            RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots.Single(x => x.Kind == entry.RootKind);
            RequireInventoryTargetAbsent(root, entry.RelativePath);
            RequireInventoryTargetAbsent(root, entry.TempRelativePath);
        }
        foreach (var root in roots)
            RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

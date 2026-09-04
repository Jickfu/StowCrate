using System.Collections.Immutable;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Infrastructure.Filesystem;

public sealed partial class StorageRelocationPhysicalStore : IStorageRelocationInventoryProbe
{
    public async Task<StorageRelocationPhysicalInventory> ObserveInventoryAsync(StorageRelocationInventory inventory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInventory(inventory);
        var roots = inventory.Roots.Select(x => new StorageRelocationRoot(x.Kind, x.OldRoot, x.NewRoot,
            InspectIdentity(x.OldRoot.CanonicalPath, true), InspectIdentity(x.NewRoot.CanonicalPath, true))).ToImmutableArray();
        var identities = roots.SelectMany(x => new[] { x.OldIdentity, x.NewIdentity }).ToArray();
        if (identities.Distinct().Count() != identities.Length)
            throw new IOException("Relocation inventory roots alias the same object.");

        var entries = ImmutableArray.CreateBuilder<StorageRelocationPlacementObservation>();
        foreach (var entry in inventory.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots.Single(x => x.Kind == entry.RootKind);
            RequireInventoryTargetAbsent(root, entry.RelativePath);
            var source = Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
            var identity = InspectIdentity(source, false);
            await VerifyAsync(source, identity, entry.Artifact, cancellationToken).ConfigureAwait(false);
            entries.Add(new(entry, identity));
        }
        // 容量观察必须绑定本次捕获的目标根，不能把替换后的目录空间用于原观察。
        var summaries = await capacity.CheckAsync(roots.Select(root => new StorageCapacityNeed(root.NewRoot,
            inventory.Entries.Where(x => x.RootKind == root.Kind).Aggregate(0L, (sum, x) => checked(sum + x.Artifact.Length)),
            root.NewIdentity)), cancellationToken).ConfigureAwait(false);

        // 后续文件哈希或容量 I/O 期间，较早条目可能被替换；返回前重验整个 namespace。
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireIdentity(root.OldRoot.CanonicalPath, true, root.OldIdentity);
            RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
        }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots.Single(x => x.Kind == entry.Placement.RootKind);
            RequireIdentity(Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.Placement.RelativePath), false, entry.Identity);
            RequireInventoryTargetAbsent(root, entry.Placement.RelativePath);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(inventory, roots, entries.ToImmutable(), summaries);
    }

    private static void RequireInventoryTargetAbsent(StorageRelocationRoot root, RelativeStoragePath relative)
    {
        RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
        var parts = relative.Value.Split('/');
        var path = root.NewRoot.CanonicalPath;
        for (var i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
            // 缺少父目录是可观察的状态，不为预览创建目录，也不沿链接判断 target absence。
            if (!Exists(path)) return;
            if (i == parts.Length - 1) throw new IOException("Relocation inventory target is occupied.");
            _ = InspectIdentity(path, true);
        }
    }

    private static void ValidateInventory(StorageRelocationInventory inventory)
    {
        if (inventory.PlanId.Value == Guid.Empty || inventory.DeviceId.Value == Guid.Empty
            || inventory.Roots.IsDefaultOrEmpty || inventory.Roots.Length > 2 || inventory.Entries.IsDefault
            || inventory.Roots.Any(x => !Enum.IsDefined(x.Kind))
            || inventory.Roots.Select(x => x.Kind).Distinct().Count() != inventory.Roots.Length
            || inventory.Entries.Select(x => x.Artifact.VersionId).Distinct().Count() != inventory.Entries.Length
            || inventory.Entries.Any(x => x.UnitId.Value == Guid.Empty || x.Artifact.VersionId.Value == Guid.Empty
                || x.Artifact.Integrity == default || x.Artifact.Length < 0 || string.IsNullOrEmpty(x.RelativePath.Value)
                || !inventory.Roots.Any(r => r.Kind == x.RootKind)))
            throw new ArgumentException("Invalid relocation inventory.", nameof(inventory));
        var paths = inventory.Roots.SelectMany(x => new[] { x.OldRoot, x.NewRoot }).ToArray();
        for (var i = 0; i < paths.Length; i++)
        for (var j = i + 1; j < paths.Length; j++)
            if (paths[i].Overlaps(paths[j])) throw new ArgumentException("Relocation inventory roots overlap.", nameof(inventory));
        foreach (var entry in inventory.Entries)
            foreach (var segment in entry.RelativePath.Value.Split('/'))
                if (Path.IsPathRooted(segment) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new IOException("Relocation inventory path is not representable on this platform.");
    }
}

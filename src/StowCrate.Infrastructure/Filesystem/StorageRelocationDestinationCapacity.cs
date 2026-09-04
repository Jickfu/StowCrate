using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Filesystem;

public sealed partial class StorageRelocationPhysicalStore
{
    private async Task<ImmutableArray<StorageCapacitySummary>> CheckDestinationCapacityAsync(
        IReadOnlyList<StorageRelocationRoot> roots, IReadOnlyList<StorageRelocationPlacement> entries, CancellationToken token)
    {
        var before = CaptureDestinationNeeds(roots, entries, token);
        var result = await capacity.CheckAsync(before, token).ConfigureAwait(false);
        // 查询期间新增目录或替换挂载位置时，旧卷容量不能继续授权当前目标布局。
        var after = CaptureDestinationNeeds(roots, entries, token);
        if (!before.SequenceEqual(after))
            throw new StorageRelocationCapacityException(StorageRelocationCapacityFailure.Unavailable);
        return result;
    }

    private static ImmutableArray<StorageCapacityNeed> CaptureDestinationNeeds(
        IReadOnlyList<StorageRelocationRoot> roots, IReadOnlyList<StorageRelocationPlacement> entries, CancellationToken token)
    {
        var needs = ImmutableArray.CreateBuilder<StorageCapacityNeed>();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
            var selected = entries.Where(x => x.RootKind == root.Kind).ToArray();
            if (selected.Length == 0) needs.Add(new(root.NewRoot, 0, root.NewIdentity));
            foreach (var entry in selected)
            {
                token.ThrowIfCancellationRequested();
                var parent = root.NewRoot.CanonicalPath;
                var identity = root.NewIdentity;
                foreach (var segment in entry.RelativePath.Value.Split('/').SkipLast(1))
                {
                    token.ThrowIfCancellationRequested();
                    var child = Path.Combine(parent, segment);
                    if (!Exists(child)) break;
                    identity = InspectIdentity(child, true);
                    parent = child;
                }
                // 复制写到 final 的同目录 temp；不存在的父目录将建在最近现存父目录所在卷。
                // 此路径只用于容量查询，不把它当作新的 binding 或 portable identity。
                var key = parent.Replace('\\', '/');
                var location = new ResolvedPhysicalPath(parent, key.Length > 1 ? key.TrimEnd('/') : key);
                needs.Add(new(location, entry.Artifact.Length, identity));
            }
            RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
        }
        return needs.ToImmutable();
    }
}

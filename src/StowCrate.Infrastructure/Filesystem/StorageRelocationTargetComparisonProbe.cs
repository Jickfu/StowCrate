using System.Text;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Filesystem;

/// <summary>验证已支持的字节精确目录语义；未知能力不降级为操作系统默认比较。</summary>
public sealed class StorageRelocationTargetComparisonProbe : IStorageRelocationTargetComparisonProbe
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<string, StorageObjectIdentity> observeDirectory;

    public StorageRelocationTargetComparisonProbe() : this(LinuxOrdinalDirectoryProbe.Observe) { }

    // 仅供程序集内测试注入查询故障/漂移；产品入口不接受用户指定比较规则。
    internal StorageRelocationTargetComparisonProbe(Func<string, StorageObjectIdentity> observeDirectory)
        => this.observeDirectory = observeDirectory;

    public async Task VerifyTargetsAsync(StorageRelocationPhysicalInventory observation, Guid transactionId, CancellationToken cancellationToken)
    {
        var physical = new StorageRelocationPhysicalStore();
        // 复用完整集合、事务 temp、字面冲突和 no-follow 占用验证，不构造占位 manifest。
        await physical.VerifyUnoccupiedTargetsAsync(observation, transactionId, cancellationToken).ConfigureAwait(false);
        var before = Capture(observation, transactionId, cancellationToken);
        var after = Capture(observation, transactionId, cancellationToken);
        if (!before.SequenceEqual(after)) throw new IOException("Relocation target comparison namespace changed.");
        await physical.VerifyUnoccupiedTargetsAsync(observation, transactionId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task VerifyLayoutAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        // manifest 已校验 final/temp 字面冲突和事务路径。这里只投影布局，不冒充新 inventory/hash 观察。
        var roots = manifest.Roots;
        var entries = manifest.Entries.Select(x => new StorageRelocationPlacement(x.UnitId, x.RootKind, x.Artifact, x.RelativePath)).ToArray();
        var before = Capture(roots, entries, manifest.TransactionId, cancellationToken);
        var after = Capture(roots, entries, manifest.TransactionId, cancellationToken);
        if (!before.SequenceEqual(after)) throw new IOException("Relocation target comparison namespace changed.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private List<DirectoryObservation> Capture(StorageRelocationPhysicalInventory observation, Guid transactionId, CancellationToken token)
        => Capture(observation.Roots, observation.Entries.Select(x => x.Placement).ToArray(), transactionId, token);

    private List<DirectoryObservation> Capture(IReadOnlyList<StorageRelocationRoot> roots,
        IReadOnlyList<StorageRelocationPlacement> entries, Guid transactionId, CancellationToken token)
    {
        var result = new List<DirectoryObservation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<StorageObjectIdentity>();
        foreach (var root in roots)
        {
            try { _ = StrictUtf8.GetByteCount(root.NewRoot.CanonicalPath); }
            catch (EncoderFallbackException) { throw new StorageRelocationComparisonUnavailableException(); }
            AddDirectory(root.NewRoot.CanonicalPath, root.NewIdentity);
            foreach (var entry in entries.Where(x => x.RootKind == root.Kind))
            {
                var target = entry.RelativePath;
                var temp = StorageRelocationTempLayout.Create(transactionId, entry.Artifact.VersionId, target);
                // UTF-8 替换回退可能使不同字符串变成同一文件名，必须严格验证 final 和较长的 temp。
                foreach (var relative in new[] { target, temp })
                    foreach (var segment in relative.Value.Split('/'))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (StrictUtf8.GetByteCount(segment) > 255)
                                throw new IOException("Relocation target component exceeds the supported filesystem limit.");
                        }
                        catch (EncoderFallbackException) { throw new StorageRelocationComparisonUnavailableException(); }
                    }
                var parent = root.NewRoot.CanonicalPath;
                foreach (var segment in target.Value.Split('/').SkipLast(1))
                {
                    token.ThrowIfCancellationRequested();
                    parent = Path.Combine(parent, segment);
                    try { _ = File.GetAttributes(parent); }
                    catch (FileNotFoundException) { AddMissing(parent); break; }
                    catch (DirectoryNotFoundException) { AddMissing(parent); break; }
                    AddDirectory(parent, null);
                }
            }
        }
        return result;

        void AddMissing(string path)
        {
            // 首个缺失目录继承最近现存目录的已验证 ext 比较规则；不创建任何目录。
            if (seen.Add(path)) result.Add(new(path, null));
        }

        void AddDirectory(string path, StorageObjectIdentity? expected)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(path)) return;
            var identity = StorageRelocationPhysicalStore.InspectIdentity(path, true);
            if (expected is not null && identity != expected) throw new IOException("Relocation target root changed.");
            StorageObjectIdentity observed;
            try { observed = observeDirectory(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
                or EntryPointNotFoundException or DllNotFoundException)
            { throw new StorageRelocationComparisonUnavailableException(); }
            token.ThrowIfCancellationRequested();
            if (observed != identity || StorageRelocationPhysicalStore.InspectIdentity(path, true) != identity)
                throw new IOException("Relocation comparison directory changed.");
            // 同一目录出现在不同目标路径（例如 bind mount）时，不把字面不同当作互不冲突。
            if (!identities.Add(identity)) throw new IOException("Relocation target directories alias the same object.");
            result.Add(new(path, identity));
        }
    }

    private sealed record DirectoryObservation(string Path, StorageObjectIdentity? Identity);
}

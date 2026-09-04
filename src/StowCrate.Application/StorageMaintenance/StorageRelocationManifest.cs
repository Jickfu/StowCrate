using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.StorageMaintenance;

public enum StorageRootKind { Current = 0, History = 1 }
public sealed record StorageRelocationRoot(StorageRootKind Kind, ResolvedPhysicalPath OldRoot, ResolvedPhysicalPath NewRoot,
    StorageObjectIdentity OldIdentity, StorageObjectIdentity NewIdentity);
public sealed record StorageRelocationEntry(ArchiveUnitId UnitId, StorageRootKind RootKind, StorageTransferArtifact Artifact,
    RelativeStoragePath RelativePath, RelativeStoragePath TempRelativePath, StorageObjectIdentity OldIdentity);

/// <summary>仅 root relocation 的冻结清单。Output Reorganization 使用同一进度协议，但需要独立的 placement/layout 清单。</summary>
public sealed class StorageRelocationManifest
{
    public StorageRelocationManifest(Guid transactionId, PlanId planId, DeviceId deviceId, Sha256Digest executionSemanticDigest,
        IEnumerable<StorageRelocationRoot> roots, IEnumerable<StorageRelocationEntry> entries)
    {
        if (transactionId == Guid.Empty || planId.Value == Guid.Empty || deviceId.Value == Guid.Empty || executionSemanticDigest == default)
            throw new ArgumentException("Relocation requires complete transaction, device and execution identities.");
        ArgumentNullException.ThrowIfNull(roots); ArgumentNullException.ThrowIfNull(entries);
        Roots = roots.OrderBy(x => x.Kind).ToImmutableArray();
        Entries = entries.OrderBy(x => x.Artifact.VersionId.Value).ToImmutableArray();
        if (Roots.Length is < 1 or > 2 || Roots.Select(x => x.Kind).Distinct().Count() != Roots.Length)
            throw new ArgumentException("Relocation needs one or two distinct root slots.", nameof(roots));
        foreach (var root in Roots)
        {
            if (!Enum.IsDefined(root.Kind) || root.OldRoot.Overlaps(root.NewRoot))
                throw new ArgumentException("Old and new roots must not overlap.", nameof(roots));
            ValidateIdentity(root.OldIdentity); ValidateIdentity(root.NewIdentity);
            if (root.OldIdentity == root.NewIdentity) throw new ArgumentException("Roots alias the same object.", nameof(roots));
        }
        var allRoots = Roots.SelectMany(x => new[] { x.OldRoot, x.NewRoot }).ToArray();
        for (var i = 0; i < allRoots.Length; i++)
        for (var j = i + 1; j < allRoots.Length; j++)
            if (allRoots[i].Overlaps(allRoots[j])) throw new ArgumentException("Relocation root sets overlap.", nameof(roots));
        foreach (var entry in Entries)
        {
            if (entry.UnitId.Value == Guid.Empty || !Roots.Any(x => x.Kind == entry.RootKind))
                throw new ArgumentException("Relocation entry references an invalid unit or root.", nameof(entries));
            ValidateIdentity(entry.OldIdentity);
            if (string.IsNullOrEmpty(entry.RelativePath.Value) || string.IsNullOrEmpty(entry.TempRelativePath.Value)
                || entry.TempRelativePath != StorageRelocationTempLayout.Create(transactionId, entry.Artifact.VersionId, entry.RelativePath))
                throw new ArgumentException("Relocation temp must be the transaction-specific destination sibling.", nameof(entries));
        }
        // 真实目标 filesystem 的 case/encoding collision 仍必须由 physical preflight 验证。
        foreach (var group in Entries.GroupBy(x => x.RootKind))
        {
            var paths = group.SelectMany(x => new[] { x.RelativePath.Value, x.TempRelativePath.Value }).ToArray();
            for (var i = 0; i < paths.Length; i++)
            for (var j = i + 1; j < paths.Length; j++)
                if (paths[i] == paths[j] || paths[i].StartsWith(paths[j] + "/", StringComparison.Ordinal) || paths[j].StartsWith(paths[i] + "/", StringComparison.Ordinal))
                    throw new ArgumentException("Relocation file paths collide.", nameof(entries));
        }
        _ = StorageTransferProgress.Prepare(transactionId, planId, Entries.Select(x => x.Artifact));
        TransactionId = transactionId; PlanId = planId; DeviceId = deviceId; ExecutionSemanticDigest = executionSemanticDigest;
    }
    public Guid TransactionId { get; }
    public PlanId PlanId { get; }
    public DeviceId DeviceId { get; }
    public Sha256Digest ExecutionSemanticDigest { get; }
    public ImmutableArray<StorageRelocationRoot> Roots { get; }
    public ImmutableArray<StorageRelocationEntry> Entries { get; }
    private static void ValidateIdentity(StorageObjectIdentity identity)
    {
        if (identity is null || string.IsNullOrWhiteSpace(identity.Provider) || identity.EncodingVersion < 1 || string.IsNullOrWhiteSpace(identity.Value))
            throw new ArgumentException("Native identity is required.", nameof(identity));
    }
}

public static class StorageRelocationTempLayout
{
    public static RelativeStoragePath Create(Guid transactionId, ArchiveVersionId versionId, RelativeStoragePath target)
    {
        var slash = target.Value.LastIndexOf('/');
        var directory = slash < 0 ? "" : target.Value[..(slash + 1)];
        var name = slash < 0 ? target.Value : target.Value[(slash + 1)..];
        return new($"{directory}.{name}.stowcrate-relocate-{transactionId:N}-{versionId.Value:N}.partial");
    }
}

public sealed record StorageRelocationJournal(StorageRelocationManifest Manifest, StorageTransferProgress Progress, long Revision);

/// <summary>事务形状端口；清理只消费 durable committed journal，完成不释放 reservation。</summary>
public interface IStorageRelocationJournalStore
{
    Task<StorageRelocationJournal> ResumeRelocationEntryAsync(Guid transactionId, long expectedRevision, ArchiveVersionId versionId, IStorageRelocationPhysicalStore physical, CancellationToken cancellationToken);
    Task CompactRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationCompletionProbe physical, CancellationToken cancellationToken);
    Task<ImmutableArray<StorageRelocationJournal>> ListRelocationsAsync(CancellationToken cancellationToken);
    Task<StorageRelocationJournal> CleanupRelocationEntryAsync(Guid transactionId, long expectedRevision, ArchiveVersionId versionId, IStorageRelocationOldCopyStore physical, CancellationToken cancellationToken);
    Task<StorageRelocationJournal> CompleteRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationOldCopyStore physical, CancellationToken cancellationToken);
    Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken);
    Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, StorageRelocationConfigurationObservation configuration, CancellationToken cancellationToken);
    Task<StorageRelocationJournal> CommitRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationPhysicalStore physical, CancellationToken cancellationToken);
    Task<StorageRelocationJournal?> LoadRelocationAsync(PlanId planId, CancellationToken cancellationToken);
    Task<StorageRelocationJournal> RecordRelocationStagedAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken cancellationToken);
    Task<StorageRelocationJournal> RecordRelocationTargetAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken cancellationToken);
    Task<StorageRelocationJournal> SealRelocationTargetsAsync(Guid transactionId, long expectedRevision, CancellationToken cancellationToken);
}

using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;

namespace StowCrate.Core.ChangeDetection;

public readonly record struct ArchiveVersionId
{
    public ArchiveVersionId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("ArchiveVersionId must not be empty.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
}

public enum StorageSlot { Current, History }

public readonly record struct RelativeStoragePath
{
    public RelativeStoragePath(string value) => Value = new LogicalPath(value).Value;
    public string Value { get; }
}

public sealed record ArtifactLocation(StorageSlot Slot, RelativeStoragePath RelativePath);
public enum ArchiveVersionLifecycle { Prepared, Verified, Published, Superseded }

public sealed class ArchiveVersion
{
    private ArchiveVersion(
        ArchiveVersionId id, PlanId planId, ArchiveUnitId unitId, PortableArchiveFormat archiveFormat,
        ArchiveSpecFingerprint archiveSpecFingerprint, ArchiveVersionLifecycle lifecycle,
        Sha256Digest? integrity, long? length, ArtifactLocation? location, DateTimeOffset? publishedAtUtc)
    {
        Id = id; PlanId = planId; ArchiveUnitId = unitId; ArchiveFormat = archiveFormat;
        ArchiveSpecFingerprint = archiveSpecFingerprint; Lifecycle = lifecycle; Integrity = integrity; Length = length; Location = location;
        PublishedAtUtc = publishedAtUtc?.ToUniversalTime();
    }

    public ArchiveVersionId Id { get; }
    public PlanId PlanId { get; }
    public ArchiveUnitId ArchiveUnitId { get; }
    public PortableArchiveFormat ArchiveFormat { get; }
    public ArchiveSpecFingerprint ArchiveSpecFingerprint { get; }
    public ArchiveVersionLifecycle Lifecycle { get; }
    public Sha256Digest? Integrity { get; }
    public long? Length { get; }
    public ArtifactLocation? Location { get; }
    public DateTimeOffset? PublishedAtUtc { get; }

    public static ArchiveVersion Prepare(ArchiveVersionId id, PlanId planId, ArchiveUnitId unitId, PortableArchiveFormat archiveFormat, ArchiveSpecFingerprint archiveSpecFingerprint) =>
        new(id, planId, unitId, archiveFormat, archiveSpecFingerprint, ArchiveVersionLifecycle.Prepared, null, null, null, null);

    public ArchiveVersion Verify(Sha256Digest integrity, long length)
    {
        if (Lifecycle is not ArchiveVersionLifecycle.Prepared) throw new InvalidOperationException("Only Prepared archive can become Verified.");
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new(Id, PlanId, ArchiveUnitId, ArchiveFormat, ArchiveSpecFingerprint, ArchiveVersionLifecycle.Verified, integrity, length, null, null);
    }

    public ArchiveVersion PublishCurrent(RelativeStoragePath path, DateTimeOffset publishedAtUtc)
    {
        if (Lifecycle is not ArchiveVersionLifecycle.Verified) throw new InvalidOperationException("Only Verified archive can become Published Current.");
        return new(Id, PlanId, ArchiveUnitId, ArchiveFormat, ArchiveSpecFingerprint, ArchiveVersionLifecycle.Published, Integrity, Length, new ArtifactLocation(StorageSlot.Current, path), publishedAtUtc);
    }

    public ArchiveVersion SupersedeToHistory(RelativeStoragePath path)
    {
        if (Lifecycle is not ArchiveVersionLifecycle.Published) throw new InvalidOperationException("Only Published Current can become Superseded History.");
        return new(Id, PlanId, ArchiveUnitId, ArchiveFormat, ArchiveSpecFingerprint, ArchiveVersionLifecycle.Superseded, Integrity, Length, new ArtifactLocation(StorageSlot.History, path), PublishedAtUtc);
    }
}

public sealed record CurrentVersion(PlanId PlanId, ArchiveUnitId ArchiveUnitId, ArchiveVersionId ArchiveVersionId, RelativeStoragePath RelativePath);
public sealed record CommittedOutputLayoutState(PlanId PlanId, ArchiveUnitId ArchiveUnitId, OutputLayoutFingerprint Fingerprint, RelativeStoragePath CurrentRelativePath);

public sealed record OutputReorganizationResult(CurrentVersion CurrentVersion, CommittedOutputLayoutState OutputLayout);
public static class OutputReorganization
{
    public static OutputReorganizationResult Commit(
        CurrentVersion current,
        CommittedOutputLayoutState existing,
        RelativeStoragePath newPath,
        OutputLayoutFingerprint newFingerprint)
    {
        if (current.PlanId != existing.PlanId || current.ArchiveUnitId != existing.ArchiveUnitId)
            throw new ArgumentException("Current pointer and output layout belong to different units.", nameof(existing));
        // layout-only commit保留 ArchiveVersion identity，只更新 Current location 与 committed layout state。
        return new(
            current with { RelativePath = newPath },
            existing with { Fingerprint = newFingerprint, CurrentRelativePath = newPath });
    }
}

public enum PublishIntentStage { Prepared, HistoryCaptured, CurrentPublished, MetadataCommitted }
public sealed record HistoryCaptureProof(ArchiveVersionId ArchiveVersionId, Sha256Digest VerifiedIntegrity, ArtifactLocation PublishedLocation);

public sealed class PendingPublishIntent
{
    private PendingPublishIntent(
        PlanId planId, ArchiveUnitId unitId, ArchiveVersionId newVersionId,
        ArchiveVersionId? oldVersionId, Sha256Digest? oldIntegrity, Sha256Digest expectedNewIntegrity,
        PublishIntentStage stage, HistoryCaptureProof? historyCapture)
    {
        PlanId = planId; ArchiveUnitId = unitId; NewVersionId = newVersionId; OldVersionId = oldVersionId;
        OldIntegrity = oldIntegrity; ExpectedNewIntegrity = expectedNewIntegrity; Stage = stage; HistoryCapture = historyCapture;
    }

    public PlanId PlanId { get; }
    public ArchiveUnitId ArchiveUnitId { get; }
    public ArchiveVersionId NewVersionId { get; }
    public ArchiveVersionId? OldVersionId { get; }
    public Sha256Digest? OldIntegrity { get; }
    public Sha256Digest ExpectedNewIntegrity { get; }
    public PublishIntentStage Stage { get; }
    public HistoryCaptureProof? HistoryCapture { get; }

    public static PendingPublishIntent Prepare(PlanId planId, ArchiveUnitId unitId, ArchiveVersionId newVersionId, Sha256Digest expectedNewIntegrity, ArchiveVersion? oldCurrent)
    {
        if (oldCurrent is not null && oldCurrent.Lifecycle is not ArchiveVersionLifecycle.Published)
            throw new ArgumentException("Old Current must be Published.", nameof(oldCurrent));
        return new(planId, unitId, newVersionId, oldCurrent?.Id, oldCurrent?.Integrity, expectedNewIntegrity, PublishIntentStage.Prepared, null);
    }

    public PendingPublishIntent MarkHistoryCaptured(HistoryCaptureProof proof)
    {
        if (Stage is not PublishIntentStage.Prepared) throw new InvalidOperationException("History capture transition requires Prepared intent.");
        ArgumentNullException.ThrowIfNull(proof);
        if (OldVersionId is null || proof.ArchiveVersionId != OldVersionId || proof.VerifiedIntegrity != OldIntegrity
            || proof.PublishedLocation.Slot is not StorageSlot.History)
            throw new InvalidOperationException("History capture must prove the old Current hash was verified and published to History.");
        return WithStage(PublishIntentStage.HistoryCaptured, proof);
    }

    public PendingPublishIntent MarkCurrentPublished()
    {
        if (Stage is not (PublishIntentStage.Prepared or PublishIntentStage.HistoryCaptured)) throw new InvalidOperationException("Current publish transition is invalid.");
        if (OldVersionId is not null && Stage is not PublishIntentStage.HistoryCaptured)
            throw new InvalidOperationException("Old Current must be captured to History before replacement.");
        return WithStage(PublishIntentStage.CurrentPublished, HistoryCapture);
    }

    internal PendingPublishIntent MarkMetadataCommitted()
    {
        if (Stage is not PublishIntentStage.CurrentPublished) throw new InvalidOperationException("Metadata commit requires CurrentPublished intent.");
        return WithStage(PublishIntentStage.MetadataCommitted, HistoryCapture);
    }

    private PendingPublishIntent WithStage(PublishIntentStage stage, HistoryCaptureProof? historyCapture) => new(PlanId, ArchiveUnitId, NewVersionId, OldVersionId, OldIntegrity, ExpectedNewIntegrity, stage, historyCapture);
}

public sealed record DurableUnitMetadataCommitPlan(
    PendingPublishIntent CurrentPublishedIntent,
    ArchiveVersion PublishedArchive,
    CurrentVersion CurrentVersion,
    BaselineCandidate BaselineCandidate,
    CommittedOutputLayoutState OutputLayout,
    ArchiveVersion? SupersededArchive);

public sealed record DurableUnitMetadataCommitResult(
    PendingPublishIntent CompletedIntent,
    ArchiveVersion PublishedArchive,
    ArchiveVersion? SupersededArchive,
    CommittedArchiveUnitBaseline Baseline,
    CommittedOutputLayoutState OutputLayout,
    CurrentVersion CurrentVersion);

public sealed record PostCommitMaintenanceState(bool HistoryMaintenanceOutOfSync, bool OldPathCleanupPending, string? Warning)
{
    public static PostCommitMaintenanceState Success { get; } = new(false, false, null);
    public static PostCommitMaintenanceState Failed(string warning) => new(true, true, warning);
}

public static class DurableUnitMetadataCommit
{
    // 该纯函数代表未来 repository 原子事务成功后的状态投影；调用前不得提前暴露 CommittedBaseline。
    public static DurableUnitMetadataCommitResult ConfirmCommitted(DurableUnitMetadataCommitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CurrentPublishedIntent.Stage is not PublishIntentStage.CurrentPublished
            || plan.PublishedArchive.Lifecycle is not ArchiveVersionLifecycle.Published
            || plan.PublishedArchive.Id != plan.CurrentVersion.ArchiveVersionId
            || plan.PublishedArchive.ArchiveSpecFingerprint != plan.BaselineCandidate.Fingerprints.ArchiveSpec
            || plan.OutputLayout.Fingerprint != plan.BaselineCandidate.Fingerprints.OutputLayout
            || plan.PublishedArchive.PlanId != plan.CurrentPublishedIntent.PlanId
            || plan.PublishedArchive.ArchiveUnitId != plan.CurrentPublishedIntent.ArchiveUnitId
            || (plan.CurrentPublishedIntent.OldVersionId is not null
                && (plan.SupersededArchive is null
                    || plan.SupersededArchive.Id != plan.CurrentPublishedIntent.OldVersionId
                    || plan.SupersededArchive.Lifecycle is not ArchiveVersionLifecycle.Superseded
                    || plan.SupersededArchive.Location?.Slot is not StorageSlot.History)))
            throw new InvalidOperationException("Metadata commit plan is not publish-complete.");
        return new(
            plan.CurrentPublishedIntent.MarkMetadataCommitted(),
            plan.PublishedArchive,
            plan.SupersededArchive,
            new CommittedArchiveUnitBaseline(plan.PublishedArchive.PlanId, plan.PublishedArchive.ArchiveUnitId, plan.BaselineCandidate),
            plan.OutputLayout,
            plan.CurrentVersion);
    }
}

public enum PublishRecoveryAction { AbortOrResumeOldCurrent, CompleteMetadataCommit, AmbiguousPublishRecovery }
public static class PublishRecoveryDecider
{
    public static PublishRecoveryAction Decide(PendingPublishIntent intent, Sha256Digest? observedCurrentIntegrity)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (observedCurrentIntegrity is not null && observedCurrentIntegrity == intent.ExpectedNewIntegrity)
            return PublishRecoveryAction.CompleteMetadataCommit;
        if (intent.OldIntegrity == observedCurrentIntegrity)
            return PublishRecoveryAction.AbortOrResumeOldCurrent;
        return PublishRecoveryAction.AmbiguousPublishRecovery;
    }
}

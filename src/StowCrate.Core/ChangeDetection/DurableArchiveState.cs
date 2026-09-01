using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;

namespace StowCrate.Core.ChangeDetection;

public readonly record struct ArchiveVersionId
{
    public ArchiveVersionId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("ArchiveVersionId must not be empty.", nameof(value)); Value = value; }
    public Guid Value { get; }
}

public readonly record struct RelativeStoragePath
{
    public RelativeStoragePath(string value) => Value = new LogicalPath(value).Value;
    public string Value { get; }
}

public enum ArchiveVersionLifecycle { Prepared, Verified, Published, Superseded }

/// <summary>归档制品 metadata；Current/History placement 由独立类型表达。</summary>
public sealed class ArchiveVersion
{
    private ArchiveVersion(ArchiveVersionId id, PlanId planId, ArchiveUnitId unitId, PortableArchiveFormat format,
        ArchiveSpecFingerprint spec, ArchiveVersionLifecycle lifecycle, Sha256Digest? integrity, long? length, DateTimeOffset? publishedAtUtc)
    { Id = id; PlanId = planId; ArchiveUnitId = unitId; ArchiveFormat = format; ArchiveSpecFingerprint = spec; Lifecycle = lifecycle; Integrity = integrity; Length = length; PublishedAtUtc = publishedAtUtc?.ToUniversalTime(); }

    public ArchiveVersionId Id { get; }
    public PlanId PlanId { get; }
    public ArchiveUnitId ArchiveUnitId { get; }
    public PortableArchiveFormat ArchiveFormat { get; }
    public ArchiveSpecFingerprint ArchiveSpecFingerprint { get; }
    public ArchiveVersionLifecycle Lifecycle { get; }
    public Sha256Digest? Integrity { get; }
    public long? Length { get; }
    public DateTimeOffset? PublishedAtUtc { get; }

    public static ArchiveVersion Prepare(ArchiveVersionId id, PlanId planId, ArchiveUnitId unitId, PortableArchiveFormat format, ArchiveSpecFingerprint spec) => new(id, planId, unitId, format, spec, ArchiveVersionLifecycle.Prepared, null, null, null);
    public ArchiveVersion Verify(Sha256Digest integrity, long length)
    {
        if (Lifecycle is not ArchiveVersionLifecycle.Prepared) throw new InvalidOperationException("Only Prepared archive can become Verified.");
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new(Id, PlanId, ArchiveUnitId, ArchiveFormat, ArchiveSpecFingerprint, ArchiveVersionLifecycle.Verified, integrity, length, null);
    }
    public ArchiveVersion Publish(DateTimeOffset publishedAtUtc)
    {
        if (Lifecycle is not ArchiveVersionLifecycle.Verified) throw new InvalidOperationException("Only Verified archive can become Published.");
        return new(Id, PlanId, ArchiveUnitId, ArchiveFormat, ArchiveSpecFingerprint, ArchiveVersionLifecycle.Published, Integrity, Length, publishedAtUtc);
    }
    public ArchiveVersion Supersede()
    {
        if (Lifecycle is not ArchiveVersionLifecycle.Published) throw new InvalidOperationException("Only Published archive can become Superseded.");
        return new(Id, PlanId, ArchiveUnitId, ArchiveFormat, ArchiveSpecFingerprint, ArchiveVersionLifecycle.Superseded, Integrity, Length, PublishedAtUtc);
    }
}

public sealed record CurrentVersion(PlanId PlanId, ArchiveUnitId ArchiveUnitId, ArchiveVersionId ArchiveVersionId, RelativeStoragePath RelativePath);
public sealed record HistoryVersionPlacement(PlanId PlanId, ArchiveUnitId ArchiveUnitId, ArchiveVersionId ArchiveVersionId, RelativeStoragePath RelativePath);
public sealed record CommittedOutputLayoutState(PlanId PlanId, ArchiveUnitId ArchiveUnitId, OutputLayoutFingerprint Fingerprint);
public sealed record OutputReorganizationResult(CurrentVersion CurrentVersion, CommittedOutputLayoutState OutputLayout);

public static class OutputReorganization
{
    public static OutputReorganizationResult Commit(CurrentVersion current, CommittedOutputLayoutState existing, RelativeStoragePath newPath, OutputLayoutFingerprint newFingerprint)
    {
        if (current.PlanId != existing.PlanId || current.ArchiveUnitId != existing.ArchiveUnitId) throw new ArgumentException("Current pointer and output layout belong to different units.", nameof(existing));
        // layout-only commit 保留 ArchiveVersion 与 baseline，只更新 Current placement 和 layout fingerprint。
        return new(current with { RelativePath = newPath }, existing with { Fingerprint = newFingerprint });
    }
}

public enum PublishIntentStage { Prepared, HistoryCaptured, CurrentPublished, MetadataCommitted }
public sealed record OldCurrentFacts(ArchiveVersion ArchiveVersion, CurrentVersion Placement);
public sealed record HistoryCaptureProof(ArchiveVersionId ArchiveVersionId, Sha256Digest VerifiedIntegrity, HistoryVersionPlacement Placement);

/// <summary>可恢复 publish journal；payload 足以在重启后重建完整 metadata commit。</summary>
public sealed class PendingPublishIntent
{
    private PendingPublishIntent(ArchiveVersion newArchive, RelativeStoragePath currentPath, BaselineCandidate baseline,
        OutputLayoutFingerprint layout, OldCurrentFacts? oldCurrent, PublishIntentStage stage, DateTimeOffset? publishedAtUtc, HistoryCaptureProof? history)
    {
        if (newArchive.Lifecycle is not ArchiveVersionLifecycle.Verified) throw new ArgumentException("Publish intent requires a Verified new archive.", nameof(newArchive));
        NewArchive = newArchive; CurrentRelativePath = currentPath; BaselineCandidate = baseline; OutputLayoutFingerprint = layout;
        OldCurrent = oldCurrent; Stage = stage; CurrentPublishedAtUtc = publishedAtUtc?.ToUniversalTime(); HistoryCapture = history;
    }

    public PlanId PlanId => NewArchive.PlanId;
    public ArchiveUnitId ArchiveUnitId => NewArchive.ArchiveUnitId;
    public ArchiveVersionId NewVersionId => NewArchive.Id;
    public Sha256Digest ExpectedNewIntegrity => NewArchive.Integrity!.Value;
    public ArchiveVersion NewArchive { get; }
    public RelativeStoragePath CurrentRelativePath { get; }
    public BaselineCandidate BaselineCandidate { get; }
    public OutputLayoutFingerprint OutputLayoutFingerprint { get; }
    public OldCurrentFacts? OldCurrent { get; }
    public PublishIntentStage Stage { get; }
    public DateTimeOffset? CurrentPublishedAtUtc { get; }
    public HistoryCaptureProof? HistoryCapture { get; }

    public static PendingPublishIntent Prepare(ArchiveVersion newArchive, RelativeStoragePath currentPath, BaselineCandidate baseline, OutputLayoutFingerprint layout, OldCurrentFacts? oldCurrent)
    {
        ArgumentNullException.ThrowIfNull(newArchive); ArgumentNullException.ThrowIfNull(baseline);
        if (newArchive.ArchiveSpecFingerprint != baseline.Fingerprints.ArchiveSpec || layout != baseline.Fingerprints.OutputLayout) throw new ArgumentException("Journal metadata must match baseline candidate.", nameof(baseline));
        if (oldCurrent is not null && (oldCurrent.ArchiveVersion.Lifecycle is not ArchiveVersionLifecycle.Published || oldCurrent.ArchiveVersion.Id != oldCurrent.Placement.ArchiveVersionId || oldCurrent.ArchiveVersion.PlanId != newArchive.PlanId || oldCurrent.ArchiveVersion.ArchiveUnitId != newArchive.ArchiveUnitId)) throw new ArgumentException("Old Current facts are inconsistent.", nameof(oldCurrent));
        return new(newArchive, currentPath, baseline, layout, oldCurrent, PublishIntentStage.Prepared, null, null);
    }

    public static PendingPublishIntent Restore(ArchiveVersion newArchive, RelativeStoragePath currentPath, BaselineCandidate baseline,
        OutputLayoutFingerprint layout, OldCurrentFacts? oldCurrent, PublishIntentStage stage,
        DateTimeOffset? currentPublishedAtUtc, HistoryCaptureProof? historyCapture)
    {
        var intent = Prepare(newArchive, currentPath, baseline, layout, oldCurrent);
        if (stage is PublishIntentStage.Prepared) return intent;
        if (historyCapture is not null) intent = intent.MarkHistoryCaptured(historyCapture);
        if (stage is PublishIntentStage.HistoryCaptured) return intent;
        if (currentPublishedAtUtc is null) throw new InvalidOperationException("Published journal stage requires a UTC timestamp.");
        intent = intent.MarkCurrentPublished(currentPublishedAtUtc.Value);
        return stage is PublishIntentStage.MetadataCommitted ? intent.MarkMetadataCommitted() : intent;
    }

    public PendingPublishIntent MarkHistoryCaptured(HistoryCaptureProof proof)
    {
        if (Stage is not PublishIntentStage.Prepared) throw new InvalidOperationException("History capture transition requires Prepared intent.");
        if (OldCurrent is null || proof.ArchiveVersionId != OldCurrent.ArchiveVersion.Id || proof.VerifiedIntegrity != OldCurrent.ArchiveVersion.Integrity
            || proof.Placement.ArchiveVersionId != proof.ArchiveVersionId || proof.Placement.PlanId != PlanId
            || proof.Placement.ArchiveUnitId != ArchiveUnitId) throw new InvalidOperationException("History proof does not match old Current facts.");
        return With(PublishIntentStage.HistoryCaptured, null, proof);
    }

    public PendingPublishIntent MarkCurrentPublished(DateTimeOffset publishedAtUtc)
    {
        if (Stage is not (PublishIntentStage.Prepared or PublishIntentStage.HistoryCaptured)) throw new InvalidOperationException("Current publish transition is invalid.");
        // History Disabled 时允许 Prepared → CurrentPublished；是否必须 capture 由 Application 的 effective policy决定。
        return With(PublishIntentStage.CurrentPublished, publishedAtUtc, HistoryCapture);
    }

    internal PendingPublishIntent MarkMetadataCommitted() => Stage is PublishIntentStage.CurrentPublished
        ? With(PublishIntentStage.MetadataCommitted, CurrentPublishedAtUtc, HistoryCapture)
        : throw new InvalidOperationException("Metadata commit requires CurrentPublished intent.");

    public DurableUnitMetadataCommitPlan RebuildMetadataCommitPlan()
    {
        if (Stage is not PublishIntentStage.CurrentPublished || CurrentPublishedAtUtc is null) throw new InvalidOperationException("Only CurrentPublished journal can rebuild metadata commit.");
        var published = NewArchive.Publish(CurrentPublishedAtUtc.Value);
        return new(this, published, new(PlanId, ArchiveUnitId, published.Id, CurrentRelativePath), BaselineCandidate,
            new(PlanId, ArchiveUnitId, OutputLayoutFingerprint), OldCurrent?.ArchiveVersion.Supersede(), HistoryCapture?.Placement);
    }

    private PendingPublishIntent With(PublishIntentStage stage, DateTimeOffset? publishedAtUtc, HistoryCaptureProof? history) => new(NewArchive, CurrentRelativePath, BaselineCandidate, OutputLayoutFingerprint, OldCurrent, stage, publishedAtUtc, history);
}

public sealed record DurableUnitMetadataCommitPlan(PendingPublishIntent CurrentPublishedIntent, ArchiveVersion PublishedArchive,
    CurrentVersion CurrentVersion, BaselineCandidate BaselineCandidate, CommittedOutputLayoutState OutputLayout,
    ArchiveVersion? SupersededArchive, HistoryVersionPlacement? HistoryPlacement);

public sealed record DurableUnitMetadataCommitResult(PendingPublishIntent CompletedIntent, ArchiveVersion PublishedArchive,
    ArchiveVersion? SupersededArchive, HistoryVersionPlacement? HistoryPlacement, CommittedArchiveUnitBaseline Baseline,
    CommittedOutputLayoutState OutputLayout, CurrentVersion CurrentVersion);

public sealed record PostCommitMaintenanceState(bool HistoryMaintenanceOutOfSync, bool OldPathCleanupPending, string? Warning)
{
    public static PostCommitMaintenanceState Success { get; } = new(false, false, null);
    public static PostCommitMaintenanceState Failed(string warning) => new(true, true, warning);
}

public static class DurableUnitMetadataCommit
{
    // 该投影代表 repository 单一原子事务成功后的状态；事务前不得暴露新 baseline。
    public static DurableUnitMetadataCommitResult ConfirmCommitted(DurableUnitMetadataCommitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CurrentPublishedIntent.Stage is not PublishIntentStage.CurrentPublished || plan.PublishedArchive.Lifecycle is not ArchiveVersionLifecycle.Published
            || plan.PublishedArchive.Id != plan.CurrentVersion.ArchiveVersionId || plan.PublishedArchive.ArchiveSpecFingerprint != plan.BaselineCandidate.Fingerprints.ArchiveSpec
            || plan.OutputLayout.Fingerprint != plan.BaselineCandidate.Fingerprints.OutputLayout || plan.PublishedArchive.PlanId != plan.CurrentPublishedIntent.PlanId
            || plan.PublishedArchive.ArchiveUnitId != plan.CurrentPublishedIntent.ArchiveUnitId || plan.CurrentVersion.PlanId != plan.PublishedArchive.PlanId
            || plan.CurrentVersion.ArchiveUnitId != plan.PublishedArchive.ArchiveUnitId || plan.OutputLayout.PlanId != plan.PublishedArchive.PlanId
            || plan.OutputLayout.ArchiveUnitId != plan.PublishedArchive.ArchiveUnitId
            || (plan.CurrentPublishedIntent.OldCurrent is not null && (plan.SupersededArchive is null || plan.SupersededArchive.Id != plan.CurrentPublishedIntent.OldCurrent.ArchiveVersion.Id || plan.SupersededArchive.Lifecycle is not ArchiveVersionLifecycle.Superseded
                || (plan.HistoryPlacement is not null && plan.HistoryPlacement.ArchiveVersionId != plan.SupersededArchive.Id))))
            throw new InvalidOperationException("Metadata commit plan is not publish-complete.");
        return new(plan.CurrentPublishedIntent.MarkMetadataCommitted(), plan.PublishedArchive, plan.SupersededArchive, plan.HistoryPlacement,
            new(plan.PublishedArchive.PlanId, plan.PublishedArchive.ArchiveUnitId, plan.PublishedArchive.Id, plan.BaselineCandidate), plan.OutputLayout, plan.CurrentVersion);
    }
}

public enum PublishRecoveryAction { AbortOrResumeOldCurrent, CompleteMetadataCommit, AmbiguousPublishRecovery }
public static class PublishRecoveryDecider
{
    public static PublishRecoveryAction Decide(PendingPublishIntent intent, Sha256Digest? observedCurrentIntegrity)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (observedCurrentIntegrity is not null && observedCurrentIntegrity == intent.ExpectedNewIntegrity) return PublishRecoveryAction.CompleteMetadataCommit;
        if (intent.OldCurrent?.ArchiveVersion.Integrity == observedCurrentIntegrity) return PublishRecoveryAction.AbortOrResumeOldCurrent;
        return PublishRecoveryAction.AmbiguousPublishRecovery;
    }
}

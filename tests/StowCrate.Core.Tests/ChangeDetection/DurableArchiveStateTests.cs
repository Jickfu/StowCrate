using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Core.Tests.ChangeDetection;

public sealed class DurableArchiveStateTests
{
    private static readonly PlanId PlanId = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly ArchiveUnitId UnitId = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly ArchiveVersionId OldId = new(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
    private static readonly ArchiveVersionId NewId = new(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
    private static readonly Sha256Digest OldHash = Hash("old");
    private static readonly Sha256Digest NewHash = Hash("new");

    [Fact]
    public void ArchiveVersionDoesNotOwnPlacementAndReorganizationOnlyMovesCurrent()
    {
        var archive = Published(OldId, OldHash);
        var current = new CurrentVersion(PlanId, UnitId, archive.Id, new("old/unit.7z"));
        var layout = new CommittedOutputLayoutState(PlanId, UnitId, Output("old"));

        var moved = OutputReorganization.Commit(current, layout, new("new/unit.7z"), Output("new"));

        Assert.Equal(OldId, archive.Id);
        Assert.Equal(OldHash, archive.Integrity);
        Assert.Equal(ArchiveVersionLifecycle.Published, archive.Lifecycle);
        Assert.Equal("new/unit.7z", moved.CurrentVersion.RelativePath.Value);
        Assert.Equal(Output("new"), moved.OutputLayout.Fingerprint);
    }

    [Fact]
    public void DurableJournalRebuildsCompleteMetadataCommitAfterRestart()
    {
        var oldArchive = Published(OldId, OldHash);
        var oldCurrent = new OldCurrentFacts(oldArchive, new(PlanId, UnitId, OldId, new("unit.7z")));
        var baseline = BaselineCandidate.FromCompleteCandidate(Fingerprints());
        var verified = ArchiveVersion.Prepare(NewId, PlanId, UnitId, PortableArchiveFormat.SevenZip, Spec()).Verify(NewHash, 20);
        var journal = PendingPublishIntent.Prepare(verified, new("unit.7z"), baseline, baseline.Fingerprints.OutputLayout, oldCurrent);
        var history = new HistoryVersionPlacement(PlanId, UnitId, OldId, new("2026/unit.7z"));

        var persisted = journal.MarkHistoryCaptured(new(OldId, OldHash, history)).MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        var committed = DurableUnitMetadataCommit.ConfirmCommitted(persisted.RebuildMetadataCommitPlan());

        Assert.Equal(NewId, committed.CurrentVersion.ArchiveVersionId);
        Assert.Equal(NewId, committed.Baseline.ArchiveVersionId);
        Assert.Equal(history, committed.HistoryPlacement);
        Assert.Equal(PublishIntentStage.MetadataCommitted, committed.CompletedIntent.Stage);
    }

    [Fact]
    public void CrashRecoveryUsesOnlyDurableOldOrExpectedNewIntegrity()
    {
        var verified = ArchiveVersion.Prepare(NewId, PlanId, UnitId, PortableArchiveFormat.SevenZip, Spec()).Verify(NewHash, 20);
        var old = new OldCurrentFacts(Published(OldId, OldHash), new(PlanId, UnitId, OldId, new("unit.7z")));
        var intent = PendingPublishIntent.Prepare(verified, new("unit.7z"), BaselineCandidate.FromCompleteCandidate(Fingerprints()), Output("layout"), old);

        Assert.Equal(PublishRecoveryAction.AbortOrResumeOldCurrent, PublishRecoveryDecider.Decide(intent, OldHash));
        Assert.Equal(PublishRecoveryAction.CompleteMetadataCommit, PublishRecoveryDecider.Decide(intent, NewHash));
        Assert.Equal(PublishRecoveryAction.AmbiguousPublishRecovery, PublishRecoveryDecider.Decide(intent, Hash("other")));
    }

    [Fact]
    public void PublishingUnverifiedArtifactIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => ArchiveVersion.Prepare(NewId, PlanId, UnitId, PortableArchiveFormat.TarZstd, Spec()).Publish(DateTimeOffset.UnixEpoch));

    private static ArchiveVersion Published(ArchiveVersionId id, Sha256Digest hash) =>
        ArchiveVersion.Prepare(id, PlanId, UnitId, PortableArchiveFormat.SevenZip, Spec()).Verify(hash, 10).Publish(DateTimeOffset.UnixEpoch);

    private static CandidateArchiveFingerprints Fingerprints()
    {
        var diagnostic = new DiagnosticFingerprint(Hash("component"));
        return new(1, new(1, 1, 1), true, new(Hash("entry")), new(Hash("selection")), Spec(), Output("layout"),
            new(Hash("semantic")), new(Hash("binding")), new(diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic));
    }

    private static ArchiveSpecFingerprint Spec() => new(Hash("spec"));
    private static OutputLayoutFingerprint Output(string value) => new(Hash(value));
    private static Sha256Digest Hash(string value) => CanonicalFingerprintEncodingV1.Encode("test", writer => writer.Utf8(1, value));
}

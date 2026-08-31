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
    public void OldCurrentMustBeCapturedToHistoryBeforeReplacement()
    {
        var old = ArchiveVersion.Prepare(OldId, PlanId, UnitId, PortableArchiveFormat.SevenZip, new ArchiveSpecFingerprint(Hash("spec")))
            .Verify(OldHash, 3).PublishCurrent(new RelativeStoragePath("unit.7z"), DateTimeOffset.UnixEpoch);
        var intent = PendingPublishIntent.Prepare(PlanId, UnitId, NewId, NewHash, old);

        Assert.Throws<InvalidOperationException>(() => intent.MarkCurrentPublished());
        var proof = new HistoryCaptureProof(OldId, OldHash, new ArtifactLocation(StorageSlot.History, new RelativeStoragePath("history/unit.7z")));
        Assert.Equal(PublishIntentStage.CurrentPublished, intent.MarkHistoryCaptured(proof).MarkCurrentPublished().Stage);
    }

    [Fact]
    public void CrashRecoveryUsesOnlyOldOrExpectedNewIntegrityAndNeverGuesses()
    {
        var old = ArchiveVersion.Prepare(OldId, PlanId, UnitId, PortableArchiveFormat.Zip, new ArchiveSpecFingerprint(Hash("spec")))
            .Verify(OldHash, 3).PublishCurrent(new RelativeStoragePath("unit.zip"), DateTimeOffset.UnixEpoch);
        var intent = PendingPublishIntent.Prepare(PlanId, UnitId, NewId, NewHash, old);

        Assert.Equal(PublishRecoveryAction.AbortOrResumeOldCurrent, PublishRecoveryDecider.Decide(intent, OldHash));
        Assert.Equal(PublishRecoveryAction.CompleteMetadataCommit, PublishRecoveryDecider.Decide(intent, NewHash));
        Assert.Equal(PublishRecoveryAction.AmbiguousPublishRecovery, PublishRecoveryDecider.Decide(intent, Hash("other")));
    }

    [Fact]
    public void OutputReorganizationKeepsArchiveVersionIdentity()
    {
        var current = new CurrentVersion(PlanId, UnitId, OldId, new RelativeStoragePath("old/unit.7z"));
        var oldFingerprint = new OutputLayoutFingerprint(Hash("old-layout"));
        var state = new CommittedOutputLayoutState(PlanId, UnitId, oldFingerprint, current.RelativePath);

        var moved = OutputReorganization.Commit(current, state, new RelativeStoragePath("new/unit.7z"), new OutputLayoutFingerprint(Hash("new-layout")));

        Assert.Equal(OldId, moved.CurrentVersion.ArchiveVersionId);
        Assert.Equal("new/unit.7z", moved.CurrentVersion.RelativePath.Value);
        Assert.NotEqual(oldFingerprint, moved.OutputLayout.Fingerprint);
    }

    [Fact]
    public void ArchiveVersionLifecycleRejectsPublishingUnverifiedArtifact()
    {
        var prepared = ArchiveVersion.Prepare(NewId, PlanId, UnitId, PortableArchiveFormat.TarZstd, new ArchiveSpecFingerprint(Hash("spec")));
        Assert.Throws<InvalidOperationException>(() => prepared.PublishCurrent(new RelativeStoragePath("unit.tar.zst"), DateTimeOffset.UnixEpoch));
        var published = prepared.Verify(NewHash, 10).PublishCurrent(new RelativeStoragePath("unit.tar.zst"), DateTimeOffset.UnixEpoch);
        Assert.Equal(ArchiveVersionLifecycle.Published, published.Lifecycle);
        Assert.Equal(StorageSlot.Current, published.Location!.Slot);
    }

    private static Sha256Digest Hash(string value) => CanonicalFingerprintEncodingV1.Encode("test", writer => writer.Utf8(1, value));
}

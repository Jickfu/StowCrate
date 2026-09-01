using StowCrate.Application.LocalState;
using StowCrate.Application.Publishing;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Paths;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed class ArchivePhysicalPublisherTests
{
    private static readonly PlanId Plan = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly ArchiveUnitId Unit = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));

    [Fact]
    public async Task CapturesHistoryByVerifiedCopyAndNeverMovesOldCurrent()
    {
        var current = Directory.CreateTempSubdirectory("stowcrate-current-");
        var history = Directory.CreateTempSubdirectory("stowcrate-history-");
        try
        {
        var bytes = "old-current"u8.ToArray(); var hash = Sha256Digest.Hash(bytes);
        await File.WriteAllBytesAsync(Path.Combine(current.FullName, "unit.7z"), bytes, CancellationToken.None);
        var version = ArchiveVersion.Prepare(new(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")), Plan, Unit,
            PortableArchiveFormat.SevenZip, new(hash)).Verify(hash, bytes.Length).Publish(new DateTimeOffset(2026, 9, 1, 17, 23, 15, 123, TimeSpan.Zero));
        var old = new OldCurrentFacts(version, new(Plan, Unit, version.Id, new("unit.7z")));
        var path = HistoryPhysicalLayoutV1.Create(Unit, version);

        var proof = await new ArchivePhysicalPublisher().CaptureHistoryAsync(old, Root(current.FullName), Root(history.FullName), path, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(current.FullName, "unit.7z")));
        Assert.Equal(hash, proof.ObservedSha256);
        Assert.Equal($"history-v1/{Unit.Value:D}/20260901T172315.123Z--{version.Id.Value:D}.7z", path.Value);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(history.FullName, path.Value.Replace('/', Path.DirectorySeparatorChar)), CancellationToken.None));
        }
        finally { current.Delete(recursive: true); history.Delete(recursive: true); }
    }

    [Fact]
    public async Task DeleteIfMatchesFailsClosedOnUnexpectedBytes()
    {
        var root = Directory.CreateTempSubdirectory("stowcrate-delete-");
        try
        {
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "unit.zip"), "unexpected", CancellationToken.None);

        var deleted = await new ArchivePhysicalPublisher().DeleteIfMatchesAsync(Root(root.FullName), new("unit.zip"),
            Sha256Digest.Hash("expected"u8), 8, CancellationToken.None);

        Assert.False(deleted);
        Assert.True(File.Exists(Path.Combine(root.FullName, "unit.zip")));
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishesCurrentFromSiblingStagingAndRemovesTemp(bool samePath)
    {
        var current = Directory.CreateTempSubdirectory("stowcrate-current-");
        var runtime = Directory.CreateTempSubdirectory("stowcrate-runtime-");
        try
        {
            var publisher = new ArchivePhysicalPublisher(new SuccessfulBarrier());
            var request = await Request(runtime.FullName, current.FullName, samePath ? "unit.7z" : "new/unit.7z");
            OldCurrentFacts? old = null;
            if (samePath)
            {
                var oldBytes = "old"u8.ToArray(); await File.WriteAllBytesAsync(Path.Combine(current.FullName, "unit.7z"), oldBytes);
                var oldVersion = ArchiveVersion.Prepare(new(Guid.NewGuid()), Plan, Unit, PortableArchiveFormat.SevenZip,
                    request.Artifact.ArchiveVersion.ArchiveSpecFingerprint).Verify(Sha256Digest.Hash(oldBytes), oldBytes.Length).Publish(DateTimeOffset.UnixEpoch);
                old = new(oldVersion, new(Plan, Unit, oldVersion.Id, new("unit.7z")));
            }

            var staging = await publisher.StageCurrentAsync(request, CancellationToken.None);
            Assert.Equal(Path.GetDirectoryName(Path.Combine(current.FullName, request.CurrentRelativePath.Value.Replace('/', Path.DirectorySeparatorChar))),
                Path.GetDirectoryName(Path.Combine(current.FullName, staging.RelativeStoragePath.Value.Replace('/', Path.DirectorySeparatorChar))));
            var receipt = await publisher.PublishCurrentAsync(request, staging, old, CancellationToken.None);

            var final = Path.Combine(current.FullName, request.CurrentRelativePath.Value.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(request.Artifact.ArchiveVersion.Integrity, Sha256Digest.Hash(await File.ReadAllBytesAsync(final)));
            Assert.False(File.Exists(Path.Combine(current.FullName, staging.RelativeStoragePath.Value.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(receipt.MetadataDurability.BarrierCompleted);
        }
        finally { current.Delete(true); runtime.Delete(true); }
    }

    [Fact]
    public async Task DifferentPathPublishPreservesOldUntilPostCommitCleanup()
    {
        var current = Directory.CreateTempSubdirectory("stowcrate-current-"); var runtime = Directory.CreateTempSubdirectory("stowcrate-runtime-");
        try
        {
            var oldBytes = "old"u8.ToArray(); await File.WriteAllBytesAsync(Path.Combine(current.FullName, "old.7z"), oldBytes);
            var request = await Request(runtime.FullName, current.FullName, "new.zip"); var publisher = new ArchivePhysicalPublisher(new SuccessfulBarrier());
            var oldVersion = ArchiveVersion.Prepare(new(Guid.NewGuid()), Plan, Unit, PortableArchiveFormat.SevenZip,
                request.Artifact.ArchiveVersion.ArchiveSpecFingerprint).Verify(Sha256Digest.Hash(oldBytes), oldBytes.Length).Publish(DateTimeOffset.UnixEpoch);
            var old = new OldCurrentFacts(oldVersion, new(Plan, Unit, oldVersion.Id, new("old.7z")));

            await publisher.PublishCurrentAsync(request, await publisher.StageCurrentAsync(request, CancellationToken.None), old, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(current.FullName, "old.7z")));
            Assert.True(File.Exists(Path.Combine(current.FullName, "new.zip")));
        }
        finally { current.Delete(true); runtime.Delete(true); }
    }

    [Fact]
    public async Task UnexpectedTargetAndCorruptTempFailClosed()
    {
        var current = Directory.CreateTempSubdirectory("stowcrate-current-"); var runtime = Directory.CreateTempSubdirectory("stowcrate-runtime-");
        try
        {
            var request = await Request(runtime.FullName, current.FullName, "new.7z"); var publisher = new ArchivePhysicalPublisher(new SuccessfulBarrier());
            var staging = await publisher.StageCurrentAsync(request, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(current.FullName, "new.7z"), "unexpected");
            await Assert.ThrowsAsync<IOException>(() => publisher.PublishCurrentAsync(request, staging, null, CancellationToken.None));
            Assert.Equal("unexpected", await File.ReadAllTextAsync(Path.Combine(current.FullName, "new.7z")));

            File.Delete(Path.Combine(current.FullName, "new.7z"));
            await File.AppendAllTextAsync(Path.Combine(current.FullName, staging.RelativeStoragePath.Value), "corrupt");
            await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishCurrentAsync(request, staging, null, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(current.FullName, "new.7z")));
        }
        finally { current.Delete(true); runtime.Delete(true); }
    }

    private static async Task<ArchivePublishRequest> Request(string runtimeRoot, string currentRoot, string relativePath)
    {
        var bytes = "new-current"u8.ToArray(); var artifactPath = Path.Combine(runtimeRoot, "artifact.partial"); await File.WriteAllBytesAsync(artifactPath, bytes);
        var digest = Sha256Digest.Hash(bytes); var spec = new ArchiveSpecFingerprint(Hash("spec")); var layout = new OutputLayoutFingerprint(Hash("layout"));
        var version = ArchiveVersion.Prepare(new(Guid.NewGuid()), Plan, Unit, PortableArchiveFormat.SevenZip, spec).Verify(digest, bytes.Length);
        var manifest = new ArchiveManifestV1(1, 1, Plan, new(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")), Unit,
            new LogicalPath("unit"), new(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection()), []);
        var fingerprints = Fingerprints(spec, layout); var snapshot = Snapshot();
        return new(new(artifactPath, version, manifest), BaselineCandidate.FromCompleteCandidate(fingerprints), layout,
            new(relativePath), new(relativePath), new EffectiveHistoryDisabled(), snapshot, Root(currentRoot), null);
    }

    private static CandidateArchiveFingerprints Fingerprints(ArchiveSpecFingerprint spec, OutputLayoutFingerprint layout)
    {
        var diagnostic = new DiagnosticFingerprint(Hash("component"));
        return new(1, new(1, 1, 1), true, new(Hash("entry")), new(Hash("selection")), spec, layout,
            new(Hash("semantic")), new(Hash("binding")), new(diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic));
    }

    private static ExecutionSemanticSnapshot Snapshot() => new(Plan, new(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")), null,
        new(Hash("plan")), [new(Unit, new(Hash("semantic")), new(Hash("binding")), null, null, new(Hash("history")))]);
    private static Sha256Digest Hash(string value) => CanonicalFingerprintEncodingV1.Encode("test", writer => writer.Utf8(1, value));
    private sealed class SuccessfulBarrier : IArchivePublishMetadataDurabilityBarrier
    { public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken) => Task.FromResult(new PublishMetadataDurabilityProof(true, "test")); }

    private static OutputRootLocalBinding Root(string path) => new(path, path, true);
}

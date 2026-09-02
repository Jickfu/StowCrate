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

    [Fact]
    public async Task RetentionDeleteRequiresExactOrdinaryArtifactAndDurabilityBarrier()
    {
        var root = Directory.CreateTempSubdirectory("stowcrate-retention-");
        try
        {
            var bytes = "retained-history"u8.ToArray(); var hash = Sha256Digest.Hash(bytes); var relative = new RelativeStoragePath("history-v1/unit/version.7z");
            var full = Path.Combine(root.FullName, relative.Value.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllBytesAsync(full, bytes);
            var intent = new RetentionDeletionIntent(new(Guid.NewGuid()), Plan, Unit, new(Guid.NewGuid()), RetentionDeletionStage.Prepared,
                relative, hash, bytes.Length, 1, 1, DateTimeOffset.UtcNow);
            var barrier = new SuccessfulBarrier(); var publisher = new ArchivePhysicalPublisher(barrier);

            var result = await publisher.DeleteDurablyIfMatchesAsync(Root(root.FullName), intent, CancellationToken.None);

            Assert.Equal(HistoryDeletionPhysicalStatus.DeletedDurably, result.Status); Assert.False(File.Exists(full)); Assert.True(barrier.Calls > 0);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task RetentionDeletePreservesMismatchedArtifactAndDoesNotCrossFailedBarrier()
    {
        var root = Directory.CreateTempSubdirectory("stowcrate-retention-safe-");
        try
        {
            var relative = new RelativeStoragePath("history-v1/unit/version.7z"); var full = Path.Combine(root.FullName, relative.Value.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, "unexpected"); var intent = new RetentionDeletionIntent(new(Guid.NewGuid()), Plan, Unit, new(Guid.NewGuid()), RetentionDeletionStage.Prepared,
                relative, Sha256Digest.Hash("expected"u8), 8, 1, 1, DateTimeOffset.UtcNow);
            var mismatch = await new ArchivePhysicalPublisher(new SuccessfulBarrier()).DeleteDurablyIfMatchesAsync(Root(root.FullName), intent, CancellationToken.None);
            Assert.Equal(HistoryDeletionPhysicalStatus.Mismatch, mismatch.Status); Assert.True(File.Exists(full));

            File.Delete(full); var absent = await new ArchivePhysicalPublisher(new FailingBarrier()).DeleteDurablyIfMatchesAsync(Root(root.FullName), intent, CancellationToken.None);
            Assert.Equal(HistoryDeletionPhysicalStatus.Failed, absent.Status);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task RetentionReconciliationDoesNotTreatBrokenSymbolicLinkAsAbsent()
    {
        var root = Directory.CreateTempSubdirectory("stowcrate-retention-link-");
        try
        {
            var relative = new RelativeStoragePath("history-v1/unit/version.7z");
            var full = Path.Combine(root.FullName, relative.Value.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            try { File.CreateSymbolicLink(full, Path.Combine(root.FullName, "missing-target")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Windows 未启用开发者模式时创建链接需要额外权限；Linux CI 仍执行完整断链场景。
                return;
            }
            var intent = new RetentionDeletionIntent(new(Guid.NewGuid()), Plan, Unit, new(Guid.NewGuid()), RetentionDeletionStage.Completed,
                relative, Sha256Digest.Hash("expected"u8), 8, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var publisher = new ArchivePhysicalPublisher(new SuccessfulBarrier());

            Assert.False(await publisher.ConfirmAbsentDurablyAsync(Root(root.FullName), intent, CancellationToken.None));
            var result = await publisher.DeleteDurablyIfMatchesAsync(Root(root.FullName), intent, CancellationToken.None);
            Assert.Equal(HistoryDeletionPhysicalStatus.UnsupportedObject, result.Status);
            Assert.NotNull(new FileInfo(full).LinkTarget);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task RetentionDeleteFailsClosedWhenLeafIdentityChangesAfterHashing()
    {
        var root = Directory.CreateTempSubdirectory("stowcrate-retention-race-");
        try
        {
            var bytes = "expected"u8.ToArray(); var relative = new RelativeStoragePath("history-v1/unit/version.7z");
            var full = Path.Combine(root.FullName, relative.Value.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllBytesAsync(full, bytes);
            var replacement = Path.Combine(root.FullName, "replacement"); await File.WriteAllTextAsync(replacement, "replacement");
            var intent = new RetentionDeletionIntent(new(Guid.NewGuid()), Plan, Unit, new(Guid.NewGuid()), RetentionDeletionStage.Prepared,
                relative, Sha256Digest.Hash(bytes), bytes.Length, 1, 1, DateTimeOffset.UtcNow);

            var result = await new ArchivePhysicalPublisher(new SuccessfulBarrier(), new ReplaceBeforeIdentityCheck(replacement))
                .DeleteDurablyIfMatchesAsync(Root(root.FullName), intent, CancellationToken.None);

            Assert.Equal(HistoryDeletionPhysicalStatus.Mismatch, result.Status);
            Assert.Equal("replacement", await File.ReadAllTextAsync(full));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task RetentionDeleteRejectsAncestorLinkWithoutTouchingTarget()
    {
        var root = Directory.CreateTempSubdirectory("stowcrate-retention-ancestor-");
        var outside = Directory.CreateTempSubdirectory("stowcrate-retention-outside-");
        try
        {
            var bytes = "expected"u8.ToArray(); var outsideFile = Path.Combine(outside.FullName, "version.7z"); await File.WriteAllBytesAsync(outsideFile, bytes);
            var history = Directory.CreateDirectory(Path.Combine(root.FullName, "history-v1"));
            try { Directory.CreateSymbolicLink(Path.Combine(history.FullName, "unit"), outside.FullName); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
            var intent = new RetentionDeletionIntent(new(Guid.NewGuid()), Plan, Unit, new(Guid.NewGuid()), RetentionDeletionStage.Prepared,
                new("history-v1/unit/version.7z"), Sha256Digest.Hash(bytes), bytes.Length, 1, 1, DateTimeOffset.UtcNow);

            var result = await new ArchivePhysicalPublisher(new SuccessfulBarrier()).DeleteDurablyIfMatchesAsync(Root(root.FullName), intent, CancellationToken.None);

            Assert.Equal(HistoryDeletionPhysicalStatus.UnsupportedObject, result.Status); Assert.True(File.Exists(outsideFile));
        }
        finally { root.Delete(true); outside.Delete(true); }
    }

    [Fact]
    public async Task RetentionDeleteRejectsHistoryRootLinkAndAbsenceProofFailsClosed()
    {
        var container = Directory.CreateTempSubdirectory("stowcrate-retention-root-link-");
        try
        {
            var outside = Directory.CreateDirectory(Path.Combine(container.FullName, "outside"));
            var bytes = "expected"u8.ToArray();
            var relative = new RelativeStoragePath("history-v1/unit/version.7z");
            var outsideFile = Path.Combine(outside.FullName, relative.Value.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outsideFile)!); await File.WriteAllBytesAsync(outsideFile, bytes);
            var rootLink = Path.Combine(container.FullName, "history-root");
            try { Directory.CreateSymbolicLink(rootLink, outside.FullName); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
            var intent = new RetentionDeletionIntent(new(Guid.NewGuid()), Plan, Unit, new(Guid.NewGuid()), RetentionDeletionStage.Prepared,
                relative, Sha256Digest.Hash(bytes), bytes.Length, 1, 1, DateTimeOffset.UtcNow);
            var publisher = new ArchivePhysicalPublisher(new SuccessfulBarrier());

            var result = await publisher.DeleteDurablyIfMatchesAsync(Root(rootLink), intent, CancellationToken.None);

            Assert.Equal(HistoryDeletionPhysicalStatus.UnsupportedObject, result.Status);
            Assert.False(await publisher.ConfirmAbsentDurablyAsync(Root(rootLink), intent with
            {
                Stage = RetentionDeletionStage.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                HistoryRelativePath = new("history-v1/unit/absent.7z")
            }, CancellationToken.None));
            Assert.True(File.Exists(outsideFile));
        }
        finally { container.Delete(true); }
    }

    [Fact]
    public async Task RetentionDeleteFailsClosedWhenHistoryRootIdentityChanges()
    {
        var container = Directory.CreateTempSubdirectory("stowcrate-retention-root-race-");
        try
        {
            var rootPath = Path.Combine(container.FullName, "history-root");
            var parkedPath = Path.Combine(container.FullName, "parked-root");
            Directory.CreateDirectory(rootPath);
            var bytes = "expected"u8.ToArray(); var relative = new RelativeStoragePath("history-v1/unit/version.7z");
            var full = Path.Combine(rootPath, relative.Value.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllBytesAsync(full, bytes);
            var intent = new RetentionDeletionIntent(new(Guid.NewGuid()), Plan, Unit, new(Guid.NewGuid()), RetentionDeletionStage.Prepared,
                relative, Sha256Digest.Hash(bytes), bytes.Length, 1, 1, DateTimeOffset.UtcNow);

            var result = await new ArchivePhysicalPublisher(new SuccessfulBarrier(),
                    new ReplaceRootBeforeIdentityCheck(rootPath, parkedPath, relative, bytes))
                .DeleteDurablyIfMatchesAsync(Root(rootPath), intent, CancellationToken.None);

            Assert.Equal(HistoryDeletionPhysicalStatus.Mismatch, result.Status);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(full));
        }
        finally { container.Delete(true); }
    }

    [Fact]
    public async Task HistoryInventoryReadsOnlyManagedNamespaceAndReportsUnknownContent()
    {
        var root = Directory.CreateTempSubdirectory("stowcrate-history-inventory-");
        try
        {
            var managed = Directory.CreateDirectory(Path.Combine(root.FullName, "history-v1", "unit"));
            var bytes = "history"u8.ToArray(); await File.WriteAllBytesAsync(Path.Combine(managed.FullName, "version.7z"), bytes);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "user-file.txt"), "untouched");

            var entries = await new ArchivePhysicalPublisher(new SuccessfulBarrier())
                .InventoryManagedNamespaceAsync(Root(root.FullName), CancellationToken.None);

            var artifact = Assert.Single(entries.Where(x => x.Kind is HistoryInventoryEntryKind.RegularFile));
            Assert.Equal("history-v1/unit/version.7z", artifact.RelativePath.Value); Assert.Equal(Sha256Digest.Hash(bytes), artifact.Integrity);
            Assert.DoesNotContain(entries, x => x.RelativePath.Value.Contains("user-file", StringComparison.Ordinal));
        }
        finally { root.Delete(true); }
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
    { public int Calls { get; private set; } public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new PublishMetadataDurabilityProof(true, "test")); } }
    private sealed class FailingBarrier : IArchivePublishMetadataDurabilityBarrier
    { public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken) => throw new IOException("barrier failed"); }
    private sealed class ReplaceBeforeIdentityCheck(string replacement) : IHistoryDeletionTestHook
    {
        public void BeforeFinalIdentityCheck(string path) { File.Delete(path); File.Move(replacement, path); }
    }
    private sealed class ReplaceRootBeforeIdentityCheck(string rootPath, string parkedPath, RelativeStoragePath relativePath, byte[] bytes)
        : IHistoryDeletionTestHook
    {
        public void BeforeFinalIdentityCheck(string path)
        {
            Directory.Move(rootPath, parkedPath);
            var replacement = Path.Combine(rootPath, relativePath.Value.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(replacement)!);
            File.WriteAllBytes(replacement, bytes);
        }
    }

    private static OutputRootLocalBinding Root(string path) => new(path, path, true);
}

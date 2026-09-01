using StowCrate.Application.LocalState;
using StowCrate.Application.Publishing;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
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

    private static OutputRootLocalBinding Root(string path) => new(path, path, true);
}

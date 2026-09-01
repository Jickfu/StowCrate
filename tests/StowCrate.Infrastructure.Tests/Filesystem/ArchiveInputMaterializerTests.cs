using System.Collections.Immutable;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.ArchiveUnits;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed class ArchiveInputMaterializerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "stowcrate-m41-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MaterializesNormalAndExternalButDoesNotRediscoverChildBoundaryContent()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source"));
        var external = Directory.CreateDirectory(Path.Combine(root, "external"));
        Directory.CreateDirectory(Path.Combine(source.FullName, "project", "child"));
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "project", "normal.txt"), "abc");
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "project", "child", "must-not-return.txt"), "child");
        await File.WriteAllTextAsync(Path.Combine(external.FullName, "data.bin"), "xyz");
        var normal = PhysicalEntry("normal.txt", CandidateEntryOwnerKind.Normal, SourceId, null, new("project/normal.txt"), Path.Combine(source.FullName, "project", "normal.txt"));
        var mapped = PhysicalEntry("external/data.bin", CandidateEntryOwnerKind.External, null, ExternalId, new("data.bin"), Path.Combine(external.FullName, "data.bin"));
        var request = Request([normal, mapped], source.FullName, external.FullName);

        var result = await Materializer().MaterializeAsync(request, "{}"u8.ToArray(), CancellationToken.None);

        Assert.Equal(["__stowcrate__/manifest.json", "external/data.bin", "normal.txt"], result.Entries.Select(x => x.ArchivePath.Value));
        Assert.DoesNotContain(result.Entries, x => x.ArchivePath.Value.Contains("child", StringComparison.Ordinal));
        Assert.Equal("abc", await File.ReadAllTextAsync(result.Entries.Single(x => x.ArchivePath.Value == "normal.txt").StagedPath));
        Assert.Equal("xyz", await File.ReadAllTextAsync(result.Entries.Single(x => x.ArchivePath.Value == "external/data.bin").StagedPath));
        await result.Workspace.CleanupAsync(false, CancellationToken.None);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("strict")]
    [InlineData("backupignore")]
    public async Task CandidateDriftStopsMaterialization(string mode)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, mode, "source"));
        Directory.CreateDirectory(Path.Combine(source.FullName, "project"));
        var name = mode == "backupignore" ? ".backupignore" : "data.txt";
        var path = Path.Combine(source.FullName, "project", name); await File.WriteAllTextAsync(path, "abc");
        var entry = PhysicalEntry(name, CandidateEntryOwnerKind.Normal, SourceId, null, new("project/" + name), path);
        entry = mode switch
        {
            "standard" => entry with { Length = entry.Length + 1 },
            "strict" => entry with { ContentIdentity = ObservedContentIdentity.FullSha256(new(new string('0', 64))) },
            _ => entry with { RawFileSha256 = new(new string('0', 64)) }
        };

        var error = await Assert.ThrowsAsync<ArchiveMaterializationException>(() => Materializer(mode).MaterializeAsync(Request([entry], source.FullName, Path.Combine(root, mode, "external")), "{}"u8.ToArray(), CancellationToken.None));
        Assert.Equal(ArchiveBuildFailureCode.InputChangedDuringMaterialization, error.Code);
    }

    [Fact]
    public async Task StagingReceivesCandidateMtimeAttributesAndUnixExecutableMetadata()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "metadata", "source")); Directory.CreateDirectory(Path.Combine(source.FullName, "project"));
        var path = Path.Combine(source.FullName, "project", "tool.sh"); await File.WriteAllTextAsync(path, "echo ok");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        var observed = new SystemPhysicalFileSystem().Inspect(path);
        var entry = new CandidateArchiveEntry(new("tool.sh"), observed.Kind, CandidateEntryOwnerKind.Normal, SourceId, null, new("project/tool.sh"),
            observed.Length, observed.LastWriteTimeUtc, ObservedContentIdentity.MetadataV1, null, null, observed.MetadataFlags);
        var result = await Materializer("metadata").MaterializeAsync(Request([entry], source.FullName, Path.Combine(root, "metadata", "external")), "{}"u8.ToArray(), CancellationToken.None);
        var staged = new SystemPhysicalFileSystem().Inspect(result.Entries.Single(x => x.ArchivePath.Value == "tool.sh").StagedPath);
        Assert.Equal(observed.LastWriteTimeUtc, staged.LastWriteTimeUtc); Assert.Equal(observed.MetadataFlags, staged.MetadataFlags);
        await result.Workspace.CleanupAsync(false, CancellationToken.None);
    }

    [Fact]
    public async Task DanglingSymbolicLinkIsInspectedAndStagedWithoutFollowingTarget()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "dangling", "source")); Directory.CreateDirectory(Path.Combine(source.FullName, "project"));
        var path = Path.Combine(source.FullName, "project", "missing-link");
        try { File.CreateSymbolicLink(path, "does-not-exist"); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        catch (IOException) when (OperatingSystem.IsWindows()) { return; }
        var observed = new SystemPhysicalFileSystem().Inspect(path); Assert.Equal(FileSystemEntryKind.Link, observed.Kind);
        var link = new LinkInfo(observed.LinkKind!.Value, observed.LinkTarget!, LinkTargetScope.Unresolved, true);
        var entry = new CandidateArchiveEntry(new("missing-link"), observed.Kind, CandidateEntryOwnerKind.Normal, SourceId, null, new("project/missing-link"),
            observed.Length, observed.LastWriteTimeUtc, ObservedContentIdentity.MetadataV1, null, link, observed.MetadataFlags);
        var result = await Materializer("dangling").MaterializeAsync(Request([entry], source.FullName, Path.Combine(root, "dangling", "external")), "{}"u8.ToArray(), CancellationToken.None);
        var staged = new SystemPhysicalFileSystem().Inspect(result.Entries.Single(x => x.ArchivePath.Value == "missing-link").StagedPath);
        Assert.Equal(FileSystemEntryKind.Link, staged.Kind); Assert.Equal("does-not-exist", staged.LinkTarget);
        await result.Workspace.CleanupAsync(false, CancellationToken.None);
    }

    private ArchiveInputMaterializer Materializer(string suffix = "ok") => new(new ArchiveBuildWorkspaceFactory(Path.Combine(root, suffix, "runtime")));

    private static ArchiveBuildRequest Request(IEnumerable<CandidateArchiveEntry> payload, string sourceRoot, string externalRoot)
    {
        var spec = new EffectiveArchiveSpec(PortableArchiveFormat.Zip, PortableCompressionPreset.Standard, new NoProtection());
        var unit = new ResolvedArchiveUnit(UnitId, SourceId, new("project"), RuleSource.UiManaged, new(), new([], [], new(), CaseSensitivity.Sensitive), spec, new EffectiveHistoryDisabled(), null, null, []);
        var entries = payload.Append(new CandidateArchiveEntry(new("__stowcrate__/manifest.json"), FileSystemEntryKind.File, CandidateEntryOwnerKind.Generated, null, null, null, 0, null, ObservedContentIdentity.MetadataV1, null, null, SourceMetadata.None));
        var candidate = new CandidateArchive(unit, new("out/project.zip"), entries, new(new("__stowcrate__/manifest.json"), 1, 1), [new LogicalPath("project/child")]);
        var capability = new ResolvedArchiveCapability(spec.Format, spec.CompressionPreset, spec.Protection, ArchiveLinkSemantics.PreserveSymbolicLinks, ArchiveMetadataSemantics.PortableBasic, true, "test");
        return new(PlanId, new(candidate, capability, unit.History, null), new(Guid.NewGuid()), new(new(new string('1', 64))),
            [new(CandidateEntryOwnerKind.Normal, SourceId, null, sourceRoot), new(CandidateEntryOwnerKind.External, null, ExternalId, externalRoot)]);
    }

    private static CandidateArchiveEntry PhysicalEntry(string archivePath, CandidateEntryOwnerKind owner, SourceId? source, ExternalSourceId? external, LogicalPath observed, string physical)
    {
        var info = new FileInfo(physical);
        return new(new(archivePath), FileSystemEntryKind.File, owner, source, external, observed, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), ObservedContentIdentity.MetadataV1, null, null, SourceMetadata.None);
    }

    private static readonly PlanId PlanId = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly SourceId SourceId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly ArchiveUnitId UnitId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly ExternalSourceId ExternalId = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

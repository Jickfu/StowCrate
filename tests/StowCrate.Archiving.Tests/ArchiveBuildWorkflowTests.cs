using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.ArchiveUnits;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.LocalState;
using StowCrate.Archiving.Manifest;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Archiving.Tests;

public sealed class ArchiveBuildWorkflowTests
{
    [Fact]
    public async Task CompleteFourLayerVerificationProducesVerifiedLifecycleOnly()
    {
        var fixture = new Fixture();
        var result = await fixture.BuildAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(ArchiveVersionLifecycle.Verified, result.Artifact!.ArchiveVersion.Lifecycle);
        Assert.Equal("aaa", result.Artifact.ArchiveVersion.Integrity!.Value.Value[..3]);
        Assert.DoesNotContain(result.Artifact.Manifest.Entries, x => x.Path.Value == "__stowcrate__/manifest.json");
        Assert.Null(result.Artifact.ArchiveVersion.PublishedAtUtc);
    }

    [Theory]
    [InlineData("materialization", ArchiveBuildFailureCode.InputChangedDuringMaterialization)]
    [InlineData("writer", ArchiveBuildFailureCode.WriterFailed)]
    [InlineData("format", ArchiveBuildFailureCode.FormatTestFailed)]
    [InlineData("entries", ArchiveBuildFailureCode.EntrySetMismatch)]
    [InlineData("manifest", ArchiveBuildFailureCode.ManifestMismatch)]
    public async Task FailureAtAnyStageNeverProducesArtifact(string stage, ArchiveBuildFailureCode expected)
    {
        var fixture = new Fixture { Failure = stage };
        var result = await fixture.BuildAsync();
        Assert.Null(result.Artifact);
        Assert.Contains(result.Diagnostics, x => x.Code == expected);
        Assert.True(fixture.Workspace.CleanupCalled);
        Assert.False(fixture.Workspace.PreservePartial);
    }

    [Fact]
    public async Task CancellationReturnsStructuredFailureAndCleansPartial()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fixture = new Fixture();
        var result = await fixture.BuildAsync(cancellation.Token);
        Assert.Contains(result.Diagnostics, x => x.Code == ArchiveBuildFailureCode.Cancelled);
        Assert.Null(result.Artifact);
    }

    [Fact]
    public async Task CleanupFailureIsExplicitDiagnostic()
    {
        var fixture = new Fixture(); fixture.Workspace.FailCleanup = true;
        var result = await fixture.BuildAsync();
        Assert.NotNull(result.Artifact);
        Assert.Contains(result.Diagnostics, x => x.Code == ArchiveBuildFailureCode.CleanupFailed && x.IsCleanupWarning);
    }

    [Fact]
    public async Task SecureLeaseIsDisposedAndOwnedBytesAreZeroized()
    {
        var fixture = new Fixture(secure: true);
        var result = await fixture.BuildAsync();
        Assert.True(result.Succeeded);
        Assert.NotNull(fixture.Writer.SecretMemory);
        Assert.True(MemoryMarshal.TryGetArray(fixture.Writer.SecretMemory!.Value, out var segment));
        Assert.All(segment.Array!, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => _ = fixture.SecretProvider.Lease!.Material);
    }

    [Fact]
    public void ManifestV1IsCanonicalRoundTripAndContainsNoRuntimeBindings()
    {
        var fixture = new Fixture(); var codec = new ArchiveManifestV1Codec();
        var first = codec.Write(fixture.Request); var second = codec.Write(fixture.Request);
        Assert.True(first.Span.SequenceEqual(second.Span));
        var parsed = codec.ReadAndValidate(first);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(x => x.Message)));
        var text = System.Text.Encoding.UTF8.GetString(first.Span);
        Assert.DoesNotContain("C:\\", text); Assert.DoesNotContain("/source", text); Assert.DoesNotContain("DeviceId", text); Assert.DoesNotContain("SecretRevision", text);
        Assert.Equal(["external/data.bin", "normal.txt"], parsed.Manifest!.Entries.Select(x => x.Path.Value));
    }

    [Fact]
    public void ManifestSchemaIsFrozenDraft202012ClosedWorldWithoutInventedId()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", "archive-manifest-v1.schema.json")))!.AsObject();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.False(schema.ContainsKey("$id"));
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(1, schema["properties"]!["schemaVersion"]!["const"]!.GetValue<int>());
    }

    [Fact]
    public async Task ReservedManifestPathIsExactlyCandidateGeneratedEntry()
    {
        var fixture = new Fixture(); var result = await fixture.BuildAsync();
        Assert.True(result.Succeeded);
        Assert.Single(fixture.Request.Archive.Candidate.Entries, x => x.OwnerKind == CandidateEntryOwnerKind.Generated && x.ArchivePath == fixture.Request.Archive.Candidate.GeneratedMetadata.ManifestPath);
        Assert.Equal(3, fixture.Verifier.LastEntries.Length);
    }

    private sealed class Fixture
    {
        private static readonly PlanId Plan = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        private static readonly SourceId Source = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
        private static readonly ArchiveUnitId UnitId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
        private static readonly ExternalSourceId External = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        private static readonly SecretSlotId Secret = new(Guid.Parse("44444444-4444-4444-8444-444444444444"));
        public Fixture(bool secure = false)
        {
            var protection = secure ? (AuthoredProtection)new SecureProtection(Secret) : new NoProtection();
            var spec = new EffectiveArchiveSpec(PortableArchiveFormat.Zip, PortableCompressionPreset.Standard, protection);
            var unit = new ResolvedArchiveUnit(UnitId, Source, new("project"), RuleSource.UiManaged, new(), new([], [], new(), CaseSensitivity.Sensitive), spec,
                new EffectiveHistoryDisabled(), null, null, []);
            var payload = new[] {
                Entry("normal.txt", CandidateEntryOwnerKind.Normal, Source, null, new LogicalPath("project/normal.txt")),
                Entry("external/data.bin", CandidateEntryOwnerKind.External, null, External, new LogicalPath("data.bin")),
                new CandidateArchiveEntry(new("__stowcrate__/manifest.json"), FileSystemEntryKind.File, CandidateEntryOwnerKind.Generated, null, null, null, 0, null, ObservedContentIdentity.MetadataV1, null, null, SourceMetadata.None)
            };
            var candidate = new CandidateArchive(unit, new("out/project.zip"), payload, new(new("__stowcrate__/manifest.json"), 1, 1), [new LogicalPath("project/child")]);
            var capability = new ResolvedArchiveCapability(spec.Format, spec.CompressionPreset, spec.Protection, ArchiveLinkSemantics.PreserveSymbolicLinks, ArchiveMetadataSemantics.PortableBasic, true, "fake-v1");
            var ready = new ExecutionReadyArchive(candidate, capability, unit.History, secure ? new(Secret, new(1)) : null);
            Request = new(Plan, ready, new(Guid.Parse("55555555-5555-4555-8555-555555555555")), new ArchiveSpecFingerprint(new(new string('2', 64))),
                [new(CandidateEntryOwnerKind.Normal, Source, null, "/source"), new(CandidateEntryOwnerKind.External, null, External, "/external")]);
            Materializer = new(this); Verifier = new(this); Writer = new(this); SecretProvider = new();
        }
        public string? Failure { get; set; }
        public ArchiveBuildRequest Request { get; }
        public FakeWorkspace Workspace { get; } = new();
        public FakeMaterializer Materializer { get; }
        public FakeVerifier Verifier { get; }
        public FakeWriter Writer { get; }
        public FakeSecretProvider SecretProvider { get; }
        public Task<ArchiveBuildResult> BuildAsync(CancellationToken token = default) => new ArchiveBuildWorkflow(Materializer, Writer, Verifier, new ArchiveManifestV1Codec(), SecretProvider).BuildAsync(Request, token);
        private static CandidateArchiveEntry Entry(string path, CandidateEntryOwnerKind owner, SourceId? source, ExternalSourceId? external, LogicalPath observed) =>
            new(new(path), FileSystemEntryKind.File, owner, source, external, observed, 3, DateTimeOffset.UnixEpoch, ObservedContentIdentity.MetadataV1, null, null, SourceMetadata.None);
    }

    private sealed class FakeMaterializer(Fixture fixture) : IArchiveInputMaterializer
    {
        public ReadOnlyMemory<byte> Manifest { get; private set; }
        public Task<MaterializedArchiveInput> MaterializeAsync(ArchiveBuildRequest request, ReadOnlyMemory<byte> manifestBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fixture.Failure == "materialization") { fixture.Workspace.CleanupAsync(false, CancellationToken.None).GetAwaiter().GetResult(); throw new ArchiveMaterializationException(ArchiveBuildFailureCode.InputChangedDuringMaterialization, "drift"); }
            Manifest = manifestBytes.ToArray();
            return Task.FromResult(new MaterializedArchiveInput(fixture.Workspace, request.Archive.Candidate.Entries.Select(x => new MaterializedArchiveEntry(x.ArchivePath, x.Kind, "/staging/" + x.ArchivePath.Value))));
        }
    }
    private sealed class FakeWriter(Fixture fixture) : IArchiveFormatWriter
    {
        public ReadOnlyMemory<byte>? SecretMemory { get; private set; }
        public Task WriteAsync(ArchiveWriteRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (fixture.Failure == "writer") throw new IOException("backend private detail");
            if (request.SecretLease is not null) SecretMemory = request.SecretLease.Material;
            return Task.CompletedTask;
        }
    }
    private sealed class FakeVerifier(Fixture fixture) : IArchiveArtifactVerifier
    {
        public ImmutableArray<ArchiveArtifactEntry> LastEntries { get; private set; }
        public Task<ArchiveArtifactVerification> VerifyAsync(string path, RelativePath manifestPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastEntries = [.. fixture.Request.Archive.Candidate.Entries.Select(x => new ArchiveArtifactEntry(x.ArchivePath, x.Kind))];
            if (fixture.Failure == "entries") LastEntries = [.. LastEntries.Skip(1)];
            var manifest = fixture.Materializer.Manifest.ToArray();
            if (fixture.Failure == "manifest")
            {
                var text = System.Text.Encoding.UTF8.GetString(manifest).Replace("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "baaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", StringComparison.Ordinal);
                manifest = System.Text.Encoding.UTF8.GetBytes(text);
            }
            return Task.FromResult(new ArchiveArtifactVerification(fixture.Failure != "format", LastEntries, manifest, new(new string('a', 64)), 123));
        }
    }
    private sealed class FakeWorkspace : IArchiveBuildWorkspace
    {
        public string StagingRoot => "/staging"; public string PartialArtifactPath => "/runtime/only.partial";
        public bool CleanupCalled { get; private set; } public bool PreservePartial { get; private set; } public bool FailCleanup { get; set; }
        public Task CleanupAsync(bool preservePartialArtifact, CancellationToken token) { CleanupCalled = true; PreservePartial = preservePartialArtifact; if (FailCleanup) throw new IOException(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeSecretProvider : IArchiveSecretLeaseProvider
    {
        public SecretMaterialLease? Lease { get; private set; }
        public Task<SecretMaterialLease?> OpenAsync(PlanId planId, SecureRevisionRequirement requirement, CancellationToken token) { Lease = new([1, 2, 3, 4]); return Task.FromResult<SecretMaterialLease?>(Lease); }
    }
}

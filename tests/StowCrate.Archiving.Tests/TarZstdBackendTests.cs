using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Archiving.Privacy;
using StowCrate.Archiving.TarZstd;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.Archiving.Tests;

public sealed class TarZstdBackendTests
{
    [Theory]
    [InlineData(PortableCompressionPreset.Fast, 1)]
    [InlineData(PortableCompressionPreset.Standard, 6)]
    [InlineData(PortableCompressionPreset.Extreme, 19)]
    public void CompressionMappingIsFrozen(PortableCompressionPreset preset, int level) => Assert.Equal(level, TarZstdSemantics.Level(preset));

    [Fact]
    public void StoreAndPrivacyAreUnsupported()
    {
        var resolver = new TarZstdCapabilityResolver();
        var metadata = new ArchiveMetadataFeatures(true, SourceMetadata.None);
        Assert.False(resolver.Resolve(new(new(PortableArchiveFormat.TarZstd, PortableCompressionPreset.Store, new NoProtection()), false, metadata), 1).IsSupported);
        Assert.False(resolver.Resolve(new(new(PortableArchiveFormat.TarZstd, PortableCompressionPreset.Standard, new PrivacyProtection()), false, metadata), 1).IsSupported);
        var supported = resolver.Resolve(new(new(PortableArchiveFormat.TarZstd, PortableCompressionPreset.Standard, new NoProtection()), false, metadata), 1);
        Assert.True(supported.IsSupported);
        Assert.Contains("checksum=true", supported.Capability!.CapabilitySemantics, StringComparison.Ordinal);
        Assert.Contains("zstdsharp=0.8.8", supported.Capability.CapabilitySemantics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriterAndVerifierStreamPaxThroughZstd()
    {
        var root = Path.Combine(Path.GetTempPath(), "stowcrate-tarzstd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var staging = Path.Combine(root, "staging"); Directory.CreateDirectory(staging);
            var manifestPath = Path.Combine(staging, "__stowcrate__", "manifest.json"); Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(manifestPath, "{\"schemaVersion\":1}");
            var payloadPath = Path.Combine(staging, "资料.txt"); await File.WriteAllTextAsync(payloadPath, "payload");
            var workspace = new TestWorkspace(staging, Path.Combine(root, "artifact.tar.zst.partial"));
            var entries = new[]
            {
                new MaterializedArchiveEntry(new("__stowcrate__/manifest.json"), FileSystemEntryKind.File, manifestPath, DateTimeOffset.UnixEpoch),
                new MaterializedArchiveEntry(new("资料.txt"), FileSystemEntryKind.File, payloadPath, DateTimeOffset.UnixEpoch, SourceMetadata.ReadOnly)
            };
            var input = new MaterializedArchiveInput(workspace, entries);
            var spec = new EffectiveArchiveSpec(PortableArchiveFormat.TarZstd, PortableCompressionPreset.Standard, new NoProtection());
            var capability = new TarZstdCapabilityResolver().Resolve(new(spec, false, new(true, SourceMetadata.ReadOnly)), 1).Capability!;
            await new TarZstdArchiveFormatWriter().WriteAsync(new(input, spec, capability, null), default);
            var verified = await new TarZstdArchiveArtifactVerifier().VerifyAsync(new(workspace.PartialArtifactPath, new("__stowcrate__/manifest.json"), null, spec, capability, null), default);
            Assert.True(verified.FormatTestPassed);
            Assert.Equal(["__stowcrate__/manifest.json", "资料.txt"], verified.Entries.Select(x => x.Path.Value));
            Assert.Equal("{\"schemaVersion\":1}", System.Text.Encoding.UTF8.GetString(verified.ManifestBytes.Span));
            Assert.True(verified.Length > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RecoveryEnvelopeIsCanonicalClosedWorldAndRandom()
    {
        var codec = new PrivacyRecoveryEnvelopeV1Codec();
        var first = codec.Create(PortableArchiveFormat.SevenZip);
        var second = codec.Create(PortableArchiveFormat.SevenZip);
        Assert.False(first.Span.SequenceEqual(second.Span));
        var parsed = codec.ReadAndValidate(first);
        Assert.True(parsed.IsValid);
        Assert.Equal(43, parsed.Envelope!.RecoveryMaterial.Length);
        Assert.DoesNotContain('=', parsed.Envelope.RecoveryMaterial);
        Assert.False(codec.ReadAndValidate(System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"extra\":true}")).IsValid);
    }

    private sealed class TestWorkspace(string stagingRoot, string partial) : IArchiveBuildWorkspace
    {
        public string StagingRoot { get; } = stagingRoot;
        public string PartialArtifactPath { get; } = partial;
        public Task CleanupAsync(bool preservePartialArtifact, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Security.Cryptography;
using System.Text;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.LocalState;
using StowCrate.Archiving.SevenZip;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.Archiving.Tests;

public sealed class SevenZipBackendTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "stowcrate-7zz-tests-" + Guid.NewGuid().ToString("N"));
    private static string BundleRoot => Environment.GetEnvironmentVariable("STOWCRATE_7ZIP_ROOT")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "bundled", "7zip"));

    [Theory]
    [InlineData(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Store, "-mx=0", "-m0=lzma2")]
    [InlineData(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Fast, "-mx=3", "-ms=on")]
    [InlineData(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, "-mx=5", "-ms=on")]
    [InlineData(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Extreme, "-mx=9", "-ms=on")]
    [InlineData(PortableArchiveFormat.Zip, PortableCompressionPreset.Store, "-mx=0", "-mm=Copy")]
    [InlineData(PortableArchiveFormat.Zip, PortableCompressionPreset.Fast, "-mx=3", "-mm=Deflate")]
    [InlineData(PortableArchiveFormat.Zip, PortableCompressionPreset.Standard, "-mx=5", "-mm=Deflate")]
    [InlineData(PortableArchiveFormat.Zip, PortableCompressionPreset.Extreme, "-mx=9", "-mm=Deflate")]
    public void CompressionMappingIsExplicitAndVersioned(PortableArchiveFormat format, PortableCompressionPreset preset, string level, string method)
    {
        var arguments = SevenZipArgumentMapping.Create(new(format, preset, new NoProtection()), "artifact.partial");
        Assert.Contains(level, arguments); Assert.Contains(method, arguments);
        Assert.DoesNotContain(arguments, value => value.StartsWith("-p", StringComparison.Ordinal));
        Assert.Contains(format is PortableArchiveFormat.SevenZip ? "-t7z" : "-tzip", arguments);
    }

    [Fact]
    public async Task ProbeRequiresPinnedExecutableHashVersionAndFormats()
    {
        var locator = new Bundled7ZipLocator(BundleRoot); var probe = new Bundled7ZipCapabilityProbe(locator, new());
        var result = await probe.ProbeAsync(CancellationToken.None);
        Assert.True(result.IsAvailable, result.Failure);
        Assert.Equal("26.02", result.Version);
        var missing = await probe.ProbeAsync(Path.Combine(temporary, "missing"), new string('0', 64), CancellationToken.None);
        Assert.Contains("missing", missing.Failure!, StringComparison.OrdinalIgnoreCase);
        var copy = Path.Combine(temporary, "tampered.exe"); Directory.CreateDirectory(temporary); File.Copy(locator.ExecutablePath, copy); await File.AppendAllTextAsync(copy, "x");
        var tampered = await probe.ProbeAsync(copy, locator.Asset.ExecutableSha256, CancellationToken.None);
        Assert.Contains("integrity", tampered.Failure!, StringComparison.OrdinalIgnoreCase);
        var system = Environment.ProcessPath!; var systemHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(system)));
        var wrong = await probe.ProbeAsync(system, systemHash, CancellationToken.None);
        Assert.Contains("version", wrong.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealSevenZipAndZipRoundTripUnicodeManifestAndEmptyDirectory()
    {
        var locator = new Bundled7ZipLocator(BundleRoot); var runner = new SevenZipProcessRunner();
        foreach (var format in new[] { PortableArchiveFormat.SevenZip, PortableArchiveFormat.Zip })
        foreach (var preset in Enum.GetValues<PortableCompressionPreset>())
        {
            var root = Path.Combine(temporary, format + "-" + preset); var staging = Path.Combine(root, "staging"); Directory.CreateDirectory(Path.Combine(staging, "空目录"));
            Directory.CreateDirectory(Path.Combine(staging, "__stowcrate__")); var unicodeFile = Path.Combine(staging, "你好.txt"); await File.WriteAllTextAsync(unicodeFile, "payload");
            var expectedMtime = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc); File.SetLastWriteTimeUtc(unicodeFile, expectedMtime);
            var manifestBytes = "{\"schemaVersion\":1}"u8.ToArray(); await File.WriteAllBytesAsync(Path.Combine(staging, "__stowcrate__", "manifest.json"), manifestBytes);
            var workspace = new Workspace(staging, Path.Combine(root, "artifact.partial"));
            var input = new MaterializedArchiveInput(workspace, [new(new("你好.txt"), FileSystemEntryKind.File, Path.Combine(staging, "你好.txt")),
                new(new("空目录"), FileSystemEntryKind.Directory, Path.Combine(staging, "空目录")), new(new("__stowcrate__/manifest.json"), FileSystemEntryKind.File, Path.Combine(staging, "__stowcrate__", "manifest.json"))]);
            var spec = new EffectiveArchiveSpec(format, preset, new NoProtection()); var capability = new ResolvedArchiveCapability(format, preset, spec.Protection, ArchiveLinkSemantics.NoLinks, ArchiveMetadataSemantics.PortableBasic, true, "integration");
            await new SevenZipArchiveFormatWriter(locator.ExecutablePath, runner).WriteAsync(new(input, spec, capability, null), CancellationToken.None);
            var verification = await new SevenZipArchiveArtifactVerifier(locator.ExecutablePath, runner).VerifyAsync(new(workspace.PartialArtifactPath,
                new("__stowcrate__/manifest.json"), spec, capability, null), CancellationToken.None);
            Assert.True(verification.FormatTestPassed); Assert.Equal(manifestBytes, verification.ManifestBytes.ToArray());
            Assert.Contains(verification.Entries, x => x.Path.Value == "你好.txt" && x.Kind == FileSystemEntryKind.File);
            Assert.Contains(verification.Entries, x => x.Path.Value == "空目录" && x.Kind == FileSystemEntryKind.Directory);
            Assert.Contains(verification.Entries, x => x.Path.Value == "__stowcrate__/manifest.json");
            var extracted = Path.Combine(root, "extracted"); var type = format is PortableArchiveFormat.SevenZip ? "-t7z" : "-tzip";
            var extraction = await runner.RunAsync(new(locator.ExecutablePath, null, ["x", type, "-y", "-bd", "-bb0", "-o" + extracted, workspace.PartialArtifactPath], null), CancellationToken.None);
            Assert.Equal(0, extraction.ExitCode); Assert.True(Directory.Exists(Path.Combine(extracted, "空目录")));
            Assert.InRange(File.GetLastWriteTimeUtc(Path.Combine(extracted, "你好.txt")), expectedMtime.AddSeconds(-2), expectedMtime.AddSeconds(2));
            var bytes = await File.ReadAllBytesAsync(workspace.PartialArtifactPath); bytes[^1] ^= 0xff; await File.WriteAllBytesAsync(workspace.PartialArtifactPath, bytes);
            var corrupted = await new SevenZipArchiveArtifactVerifier(locator.ExecutablePath, runner).VerifyAsync(new(workspace.PartialArtifactPath,
                new("__stowcrate__/manifest.json"), spec, capability, null), CancellationToken.None);
            Assert.False(corrupted.FormatTestPassed);
        }
    }

    [Fact]
    public async Task PasswordEncodingIsUtf8LineBasedAndRejectsAmbiguousMaterial()
    {
        await using var stream = new MemoryStream(); await ArchivePasswordEncoding.WriteLineAsync(stream, "密碼-αβ"u8.ToArray(), CancellationToken.None);
        Assert.Equal("密碼-αβ\n"u8.ToArray(), stream.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => ArchivePasswordEncoding.WriteLineAsync(new MemoryStream(), "bad\nsecret"u8.ToArray(), CancellationToken.None));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => ArchivePasswordEncoding.WriteLineAsync(new MemoryStream(), new byte[] { 0xff }, CancellationToken.None));
    }

    [Fact]
    public void UnsupportedProtectionLinksAndPosixMetadataNeverDowngrade()
    {
        var available = new Bundled7ZipProbeResult(true, "runtime", null, "26.02"); var resolver = new Bundled7ZipCapabilityResolver(available);
        var none = new EffectiveArchiveSpec(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection());
        Assert.True(resolver.Resolve(new(none, false, ArchiveMetadataSemantics.PortableBasic), 1).IsSupported);
        Assert.False(resolver.Resolve(new(none, true, ArchiveMetadataSemantics.PortableBasic), 1).IsSupported);
        Assert.False(resolver.Resolve(new(none, false, ArchiveMetadataSemantics.Posix), 1).IsSupported);
        Assert.False(resolver.Resolve(new(none with { Protection = new PrivacyProtection() }, false, ArchiveMetadataSemantics.PortableBasic), 1).IsSupported);
        var slot = new SecretSlotId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        Assert.False(resolver.Resolve(new(none with { Protection = new SecureProtection(slot) }, false, ArchiveMetadataSemantics.PortableBasic), 1).IsSupported);
        Assert.False(resolver.Resolve(new(none with { Format = PortableArchiveFormat.TarZstd }, false, ArchiveMetadataSemantics.PortableBasic), 1).IsSupported);
    }

    [Fact]
    public async Task ProcessCancellationKillsChildTreeAndWaitsForExit()
    {
        var executable = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC")! : "/bin/sh";
        IReadOnlyList<string> arguments = OperatingSystem.IsWindows()
            ? ["/c", "ping", "-n", "30", "127.0.0.1"] : ["-c", "sleep 30"];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SevenZipProcessRunner().RunAsync(new(executable, null, arguments, null), cancellation.Token));
    }

    public void Dispose() { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    private sealed class Workspace(string staging, string partial) : IArchiveBuildWorkspace
    {
        public string StagingRoot { get; } = staging; public string PartialArtifactPath { get; } = partial;
        public Task CleanupAsync(bool preservePartialArtifact, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

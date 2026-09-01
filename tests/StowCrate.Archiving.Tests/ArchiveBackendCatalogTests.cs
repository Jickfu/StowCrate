using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Archiving.TarZstd;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;

namespace StowCrate.Archiving.Tests;

public sealed class ArchiveBackendCatalogTests
{
    [Fact]
    public void CatalogSelectsTheOnlyExactBackend()
    {
        var backend = Registration("managed-tarzstd-v1", new TarZstdCapabilityResolver());
        var catalog = new ArchiveBackendCatalog([backend]);
        var spec = new EffectiveArchiveSpec(PortableArchiveFormat.TarZstd, PortableCompressionPreset.Standard, new NoProtection());
        var result = catalog.Resolve(new(spec, false, new(true, SourceMetadata.ReadOnly)), 1);
        Assert.True(result.IsSupported);
        Assert.Same(backend, result.Backend);
    }

    [Fact]
    public void CatalogFailsClosedForZeroAndAmbiguousMatches()
    {
        var spec = new EffectiveArchiveSpec(PortableArchiveFormat.TarZstd, PortableCompressionPreset.Standard, new NoProtection());
        var requirements = new ArchiveCapabilityRequirements(spec, false, ArchiveMetadataFeatures.None);
        Assert.False(new ArchiveBackendCatalog([]).Resolve(requirements, 1).IsSupported);
        var resolver = new TarZstdCapabilityResolver();
        var ambiguous = new ArchiveBackendCatalog([Registration("one", resolver), Registration("two", resolver)]).Resolve(requirements, 1);
        Assert.False(ambiguous.IsSupported);
        Assert.Contains("ambiguous", ambiguous.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PortableArchiveFormat.SevenZip, "Secure")]
    [InlineData(PortableArchiveFormat.Zip, "Secure")]
    [InlineData(PortableArchiveFormat.SevenZip, "Privacy")]
    [InlineData(PortableArchiveFormat.Zip, "Privacy")]
    [InlineData(PortableArchiveFormat.TarZstd, "Secure")]
    [InlineData(PortableArchiveFormat.TarZstd, "Privacy")]
    public void UnsupportedProtectionDoesNotSelectBackend(PortableArchiveFormat format, string protection)
    {
        AuthoredProtection value = protection == "Privacy" ? new PrivacyProtection() : new SecureProtection(new(Guid.NewGuid()));
        var spec = new EffectiveArchiveSpec(format, PortableCompressionPreset.Standard, value);
        var catalog = new ArchiveBackendCatalog([Registration("tar", new TarZstdCapabilityResolver())]);
        Assert.False(catalog.Resolve(new(spec, false, ArchiveMetadataFeatures.None), 1).IsSupported);
    }

    private static ArchiveBackendRegistration Registration(string identity, IArchiveCapabilityResolver resolver) =>
        new(identity, resolver, new NeverWriter(), new NeverVerifier());

    private sealed class NeverWriter : IArchiveFormatWriter
    {
        public Task WriteAsync(ArchiveWriteRequest request, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Unsupported backend must not write.");
    }
    private sealed class NeverVerifier : IArchiveArtifactVerifier
    {
        public Task<ArchiveArtifactVerification> VerifyAsync(ArchiveVerificationRequest request, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Unsupported backend must not verify.");
    }
}

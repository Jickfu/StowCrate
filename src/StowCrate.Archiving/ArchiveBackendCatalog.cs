using System.Collections.Immutable;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;

namespace StowCrate.Archiving;

/// <summary>归档后端注册只存在于Archiving；Application不感知可执行文件、library或dispatch switch。</summary>
public sealed record ArchiveBackendRegistration
{
    public ArchiveBackendRegistration(string semanticIdentity, IArchiveCapabilityResolver capabilityResolver,
        IArchiveFormatWriter writer, IArchiveArtifactVerifier verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticIdentity);
        SemanticIdentity = semanticIdentity;
        CapabilityResolver = capabilityResolver ?? throw new ArgumentNullException(nameof(capabilityResolver));
        Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        Verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }
    public string SemanticIdentity { get; }
    public IArchiveCapabilityResolver CapabilityResolver { get; }
    public IArchiveFormatWriter Writer { get; }
    public IArchiveArtifactVerifier Verifier { get; }
}

public sealed record ArchiveBackendSelection(
    ArchiveBackendRegistration? Backend,
    ResolvedArchiveCapability? Capability,
    string? Failure)
{
    public bool IsSupported => Backend is not null && Capability is not null;
}

public sealed class ArchiveBackendCatalog(IEnumerable<ArchiveBackendRegistration> registrations)
{
    private readonly ImmutableArray<ArchiveBackendRegistration> registrations = [.. registrations];

    public ArchiveBackendSelection Resolve(ArchiveCapabilityRequirements requirements, int archiveSemanticsVersion)
    {
        var matches = registrations
            .Select(registration => (Registration: registration, Resolution: registration.CapabilityResolver.Resolve(requirements, archiveSemanticsVersion)))
            .Where(candidate => candidate.Resolution.IsSupported && candidate.Resolution.Capability!.Satisfies(requirements))
            .ToArray();
        return matches.Length switch
        {
            0 => new(null, null, "No archive backend supports the exact requested capability."),
            1 => new(matches[0].Registration, matches[0].Resolution.Capability, null),
            _ => new(null, null, "Archive backend configuration is ambiguous: multiple exact backends matched.")
        };
    }

    public ArchiveBackendRegistration ResolveExact(ResolvedArchiveCapability capability)
    {
        var matches = registrations.Where(registration =>
            StringComparer.Ordinal.Equals(registration.SemanticIdentity, capability.CapabilitySemantics)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new NotSupportedException("No registered backend owns the resolved capability semantics."),
            _ => throw new InvalidOperationException("Archive backend configuration is ambiguous for the resolved capability semantics.")
        };
    }
}

public sealed class CatalogArchiveFormatWriter(ArchiveBackendCatalog catalog) : IArchiveFormatWriter
{
    public Task WriteAsync(ArchiveWriteRequest request, CancellationToken cancellationToken) =>
        catalog.ResolveExact(request.Capability).Writer.WriteAsync(request, cancellationToken);
}

public sealed class CatalogArchiveArtifactVerifier(ArchiveBackendCatalog catalog) : IArchiveArtifactVerifier
{
    public Task<ArchiveArtifactVerification> VerifyAsync(ArchiveVerificationRequest request, CancellationToken cancellationToken) =>
        catalog.ResolveExact(request.Capability).Verifier.VerifyAsync(request, cancellationToken);
}

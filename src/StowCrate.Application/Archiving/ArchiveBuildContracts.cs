using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.Application.Archiving;

public enum ArchiveBuildFailureCode
{
    InputChangedDuringMaterialization,
    MaterializationFailed,
    UnsupportedArchiveCapability,
    SecretUnavailable,
    WriterFailed,
    FormatTestFailed,
    EntrySetMismatch,
    ManifestInvalid,
    ManifestMismatch,
    IntegrityComputationFailed,
    Cancelled,
    CleanupFailed
}

public sealed record ArchiveBuildDiagnostic(ArchiveBuildFailureCode Code, string Message, RelativePath? ArchivePath = null, bool IsCleanupWarning = false);

/// <summary>仅在本轮运行中有效的物理输入 binding；不得持久化或写入 manifest。</summary>
public sealed record ArchiveInputBinding(CandidateEntryOwnerKind OwnerKind, SourceId? SourceId, ExternalSourceId? ExternalSourceId, string PhysicalRoot);

public sealed record ArchiveBuildRequest(
    PlanId PlanId,
    ExecutionReadyArchive Archive,
    ArchiveVersionId ArchiveVersionId,
    ArchiveSpecFingerprint ArchiveSpecFingerprint,
    ImmutableArray<ArchiveInputBinding> InputBindings);

/// <summary>private staging 与唯一 partial 的 runtime-only handle。</summary>
public interface IArchiveBuildWorkspace : IAsyncDisposable
{
    string StagingRoot { get; }
    string PartialArtifactPath { get; }
    Task CleanupAsync(bool preservePartialArtifact, CancellationToken cancellationToken);
}

public interface IArchiveBuildWorkspaceFactory
{
    Task<IArchiveBuildWorkspace> CreateAsync(ArchiveBuildRequest request, CancellationToken cancellationToken);
}

public sealed record MaterializedArchiveEntry(
    RelativePath ArchivePath,
    FileSystemEntryKind Kind,
    string StagedPath,
    DateTimeOffset? LastWriteTimeUtc = null,
    SourceMetadata MetadataFlags = SourceMetadata.None,
    LinkInfo? Link = null);

public sealed class MaterializedArchiveInput
{
    public MaterializedArchiveInput(IArchiveBuildWorkspace workspace, IEnumerable<MaterializedArchiveEntry> entries)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Entries = [.. entries.OrderBy(x => x.ArchivePath.Value, StringComparer.Ordinal)];
    }
    public IArchiveBuildWorkspace Workspace { get; }
    public ImmutableArray<MaterializedArchiveEntry> Entries { get; }
}

public sealed class ArchiveMaterializationException(ArchiveBuildFailureCode code, string message, RelativePath? path = null, Exception? inner = null) : Exception(message, inner)
{
    public ArchiveBuildFailureCode Code { get; } = code;
    public RelativePath? ArchivePath { get; } = path;
}

public interface IArchiveInputMaterializer
{
    Task<MaterializedArchiveInput> MaterializeAsync(ArchiveBuildRequest request, ArchiveGeneratedContent generatedContent, CancellationToken cancellationToken);
}

public sealed record ArchiveGeneratedContent(ReadOnlyMemory<byte> ManifestBytes, ReadOnlyMemory<byte>? RecoveryEnvelopeBytes = null);

public sealed record ArchiveWriteRequest(
    MaterializedArchiveInput Input,
    EffectiveArchiveSpec ArchiveSpec,
    ResolvedArchiveCapability Capability,
    SecretMaterialLease? SecretLease);

public sealed record ArchiveVerificationRequest(
    string PartialArtifactPath,
    RelativePath ManifestPath,
    RelativePath? RecoveryEnvelopePath,
    EffectiveArchiveSpec ArchiveSpec,
    ResolvedArchiveCapability Capability,
    SecretMaterialLease? SecretLease);

public interface IArchiveFormatWriter
{
    Task WriteAsync(ArchiveWriteRequest request, CancellationToken cancellationToken);
}

public sealed record ArchiveArtifactEntry(RelativePath Path, FileSystemEntryKind Kind);
public sealed record ArchiveArtifactVerification(
    bool FormatTestPassed,
    ImmutableArray<ArchiveArtifactEntry> Entries,
    ReadOnlyMemory<byte> ManifestBytes,
    Sha256Digest Sha256,
    long Length,
    ReadOnlyMemory<byte>? RecoveryEnvelopeBytes = null);

public interface IArchiveArtifactVerifier
{
    Task<ArchiveArtifactVerification> VerifyAsync(ArchiveVerificationRequest request, CancellationToken cancellationToken);
}

public interface IArchiveManifestCodec
{
    ReadOnlyMemory<byte> Write(ArchiveBuildRequest request);
    ArchiveManifestValidationResult ReadAndValidate(ReadOnlyMemory<byte> bytes);
}

public sealed record PrivacyRecoveryEnvelopeV1(
    int SchemaVersion,
    int PrivacySemanticsVersion,
    int CarrierSemanticsVersion,
    PortableArchiveFormat ArchiveFormat,
    string RecoveryMaterialEncoding,
    string RecoveryMaterial);

public sealed record PrivacyRecoveryEnvelopeValidationResult(PrivacyRecoveryEnvelopeV1? Envelope, ImmutableArray<ArchiveBuildDiagnostic> Diagnostics)
{
    public bool IsValid => Envelope is not null && Diagnostics.IsEmpty;
}

public interface IPrivacyRecoveryEnvelopeCodec
{
    ReadOnlyMemory<byte> Create(PortableArchiveFormat archiveFormat);
    PrivacyRecoveryEnvelopeValidationResult ReadAndValidate(ReadOnlyMemory<byte> bytes);
}

public interface IArchiveSecretLeaseProvider
{
    Task<SecretMaterialLease?> OpenAsync(PlanId planId, SecureRevisionRequirement requirement, CancellationToken cancellationToken);
}

public sealed record ArchiveManifestEntry(RelativePath Path, FileSystemEntryKind Kind, CandidateEntryOwnerKind OwnerKind,
    long Length, DateTimeOffset? LastWriteTimeUtc, SourceMetadata Metadata, string? LinkTarget, Sha256Digest? FullContentSha256, Sha256Digest? RawFileSha256);

public sealed record ArchiveManifestV1(
    int SchemaVersion,
    int ArchiveSemanticsVersion,
    PlanId PlanId,
    SourceId SourceId,
    ArchiveUnitId ArchiveUnitId,
    LogicalPath UnitLogicalPath,
    EffectiveArchiveSpec ArchiveSpec,
    ImmutableArray<ArchiveManifestEntry> Entries);

public sealed record ArchiveManifestValidationResult(ArchiveManifestV1? Manifest, ImmutableArray<ArchiveBuildDiagnostic> Diagnostics)
{
    public bool IsValid => Manifest is not null && Diagnostics.IsEmpty;
}

public sealed record VerifiedArchiveArtifact
{
    public VerifiedArchiveArtifact(string partialArtifactPath, ArchiveVersion archiveVersion, ArchiveManifestV1 manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialArtifactPath); ArgumentNullException.ThrowIfNull(archiveVersion); ArgumentNullException.ThrowIfNull(manifest);
        if (archiveVersion.Lifecycle is not ArchiveVersionLifecycle.Verified) throw new ArgumentException("Artifact requires a Verified ArchiveVersion.", nameof(archiveVersion));
        PartialArtifactPath = partialArtifactPath; ArchiveVersion = archiveVersion; Manifest = manifest;
    }
    public string PartialArtifactPath { get; }
    public ArchiveVersion ArchiveVersion { get; }
    public ArchiveManifestV1 Manifest { get; }
}

public sealed record ArchiveBuildResult(VerifiedArchiveArtifact? Artifact, ImmutableArray<ArchiveBuildDiagnostic> Diagnostics)
{
    public bool Succeeded => Artifact is not null && Diagnostics.All(x => x.IsCleanupWarning);
}

using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.ArchiveUnits;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.Application.BackupPlans.Candidates;

public enum CandidateEntryOwnerKind { Normal, External, Generated }

public sealed record CandidateArchiveEntry(
    RelativePath ArchivePath,
    FileSystemEntryKind Kind,
    CandidateEntryOwnerKind OwnerKind,
    SourceId? SourceId,
    ExternalSourceId? ExternalSourceId,
    LogicalPath? ObservedPath,
    long Length,
    DateTimeOffset? LastWriteTimeUtc,
    ObservedContentIdentity ContentIdentity,
    Sha256Digest? RawFileSha256,
    LinkInfo? Link,
    SourceMetadata MetadataFlags);

public sealed record GeneratedMetadataPlan(
    RelativePath ManifestPath,
    int ArchiveSemanticsVersion,
    int ManifestSchemaVersion,
    RelativePath? RecoveryEnvelopePath = null,
    int? RecoveryEnvelopeSchemaVersion = null,
    int? PrivacyCarrierSemanticsVersion = null);

public sealed class CandidateArchive
{
    public CandidateArchive(
        ResolvedArchiveUnit unit,
        LogicalPath outputRelativePath,
        IEnumerable<CandidateArchiveEntry> entries,
        GeneratedMetadataPlan generatedMetadata,
        IEnumerable<LogicalPath> childBoundaryRoots)
    {
        Unit = unit;
        OutputRelativePath = outputRelativePath;
        Entries = [.. entries.OrderBy(entry => entry.ArchivePath.Value, StringComparer.Ordinal)];
        GeneratedMetadata = generatedMetadata;
        ChildBoundaryRoots = [.. childBoundaryRoots.OrderBy(path => path.Value, StringComparer.Ordinal)];
    }

    public ResolvedArchiveUnit Unit { get; }
    public LogicalPath OutputRelativePath { get; }
    public ImmutableArray<CandidateArchiveEntry> Entries { get; }
    public GeneratedMetadataPlan GeneratedMetadata { get; }
    public ImmutableArray<LogicalPath> ChildBoundaryRoots { get; }
}

public enum CandidateCompositionIssueCode
{
    IncompleteObservation,
    ReservedNamespaceCollision,
    EntryOwnershipCollision,
    OutputPathCollision,
    MissingObservation,
    ExternalKindMismatch
}

public sealed record CandidateCompositionIssue(
    CandidateCompositionIssueCode Code,
    string Message,
    ArchiveUnitId? ArchiveUnitId = null,
    RelativePath? ArchivePath = null);

public sealed class CandidateArchiveSet
{
    public CandidateArchiveSet(
        PortableSemanticsPins semantics,
        IEnumerable<CandidateArchive> archives,
        IEnumerable<CandidateCompositionIssue> issues,
        IEnumerable<PendingArchiveUnitRegistration> pendingRegistrations)
    {
        Semantics = semantics;
        Archives = [.. archives];
        Issues = [.. issues];
        PendingRegistrations = [.. pendingRegistrations];
    }

    public PortableSemanticsPins Semantics { get; }
    public ImmutableArray<CandidateArchive> Archives { get; }
    public ImmutableArray<CandidateCompositionIssue> Issues { get; }
    public ImmutableArray<PendingArchiveUnitRegistration> PendingRegistrations { get; }
    public bool IsPreviewComplete => Issues.All(issue => issue.Code is CandidateCompositionIssueCode.IncompleteObservation);
}

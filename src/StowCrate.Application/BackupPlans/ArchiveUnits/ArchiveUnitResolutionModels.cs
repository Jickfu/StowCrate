using System.Collections.Immutable;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.BackupPlans.ArchiveUnits;

public sealed record LocalArchiveUnitIdentityRegistration(
    SourceId SourceId,
    LogicalPath Path,
    ArchiveUnitId ArchiveUnitId);

public sealed record PendingArchiveUnitRegistration(
    SourceId SourceId,
    LogicalPath Path,
    ArchiveUnitId ArchiveUnitId);

public interface IArchiveUnitIdGenerator
{
    ArchiveUnitId Generate();
}

public sealed class RandomArchiveUnitIdGenerator : IArchiveUnitIdGenerator
{
    public ArchiveUnitId Generate() => new(Guid.NewGuid());
}

public enum ArchiveUnitResolutionIssueCode
{
    IncompleteObservation,
    MissingSourceObservation,
    MissingExternalObservation,
    DuplicateSourceObservation,
    DuplicateExternalObservation,
    InvalidBackupIgnore,
    BackupIgnoreDeclarationIdMismatch,
    DuplicateObservedArchiveUnitId,
    ArchiveUnitRelocated,
    RuleSourceConflict,
    MissingFileManagedRuleSource,
    IdentityConflict,
    ExternalCrossesDiscoveredChildBoundary,
    GeneratedIdentityCollision
}

public sealed record ArchiveUnitResolutionIssue(
    ArchiveUnitResolutionIssueCode Code,
    string Message,
    SourceId? SourceId = null,
    LogicalPath? Path = null,
    ExternalSourceId? ExternalSourceId = null);

public sealed class ArchiveUnitResolutionResult
{
    public ArchiveUnitResolutionResult(
        ResolvedArchiveUnitSet? resolvedSet,
        IEnumerable<PendingArchiveUnitRegistration> pendingRegistrations,
        IEnumerable<ArchiveUnitResolutionIssue> issues)
    {
        ResolvedSet = resolvedSet;
        PendingRegistrations = [.. pendingRegistrations];
        Issues = [.. issues];
    }

    public ResolvedArchiveUnitSet? ResolvedSet { get; }
    public ImmutableArray<PendingArchiveUnitRegistration> PendingRegistrations { get; }
    public ImmutableArray<ArchiveUnitResolutionIssue> Issues { get; }
    public bool IsSuccess => ResolvedSet is not null && Issues.IsEmpty;
    public bool CanPreview => ResolvedSet is not null;
    public bool RequiresDurableRegistrationCommit => !PendingRegistrations.IsEmpty;
}

public sealed class ResolvedArchiveUnitSet
{
    public ResolvedArchiveUnitSet(
        IEnumerable<ResolvedArchiveUnit> units,
        IEnumerable<SourceObservationSnapshot> sourceObservations,
        IEnumerable<ExternalSourceSnapshot> externalObservations)
    {
        Units = [.. units];
        SourceObservations = [.. sourceObservations];
        ExternalObservations = [.. externalObservations];
    }

    public ImmutableArray<ResolvedArchiveUnit> Units { get; }
    public ImmutableArray<SourceObservationSnapshot> SourceObservations { get; }
    public ImmutableArray<ExternalSourceSnapshot> ExternalObservations { get; }
}

public sealed record ResolvedArchiveUnit(
    ArchiveUnitId ArchiveUnitId,
    SourceId SourceId,
    LogicalPath Root,
    RuleSource RuleSource,
    RuleSet LocalRuleSet,
    EffectiveRuleSet EffectiveRuleSet,
    EffectiveArchiveSpec ArchiveSpec,
    EffectiveHistoryPolicy History,
    Sha256Digest? RuleSourceObservationFingerprint,
    ArchiveUnitId? ParentArchiveUnitId,
    ImmutableArray<ArchiveUnitId> ChildArchiveUnitIds);

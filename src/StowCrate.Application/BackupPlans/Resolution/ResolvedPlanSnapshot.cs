using System.Collections.Immutable;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Application.BackupPlans.Resolution;

public sealed class ResolvedPlanSnapshot
{
    public ResolvedPlanSnapshot(
        PlanId planId,
        DeviceId deviceId,
        PortableSemanticsPins semantics,
        IEnumerable<ResolvedBackupSource> sources,
        ResolvedPhysicalPath currentRoot,
        ResolvedPhysicalPath? historyRoot,
        IEnumerable<BackupRule> globalRules,
        IEnumerable<BackupRule> planRules,
        IEnumerable<PreparedDeclaredArchiveUnit> declaredArchiveUnits,
        DefaultUnitPolicy defaultUnitPolicy,
        PortableLinkPolicy linkPolicy,
        PortableChangeDetectionMode changeDetection,
        IEnumerable<ResolvedExternalSource> externalSources,
        IEnumerable<SecretBindingFact> secretBindings)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        PlanId = planId;
        DeviceId = deviceId;
        Semantics = semantics;
        Sources = [.. sources];
        CurrentRoot = currentRoot;
        HistoryRoot = historyRoot;
        GlobalRules = [.. globalRules];
        PlanRules = [.. planRules];
        DeclaredArchiveUnits = [.. declaredArchiveUnits];
        DefaultUnitPolicy = defaultUnitPolicy;
        LinkPolicy = linkPolicy;
        ChangeDetection = changeDetection;
        ExternalSources = [.. externalSources];
        SecretBindings = [.. secretBindings];
    }

    public PlanId PlanId { get; }
    public DeviceId DeviceId { get; }
    public PortableSemanticsPins Semantics { get; }
    public ImmutableArray<ResolvedBackupSource> Sources { get; }
    public ResolvedPhysicalPath CurrentRoot { get; }
    public ResolvedPhysicalPath? HistoryRoot { get; }
    public ImmutableArray<BackupRule> GlobalRules { get; }
    public ImmutableArray<BackupRule> PlanRules { get; }
    public ImmutableArray<PreparedDeclaredArchiveUnit> DeclaredArchiveUnits { get; }
    public DefaultUnitPolicy DefaultUnitPolicy { get; }
    public PortableLinkPolicy LinkPolicy { get; }
    public PortableChangeDetectionMode ChangeDetection { get; }
    public ImmutableArray<ResolvedExternalSource> ExternalSources { get; }
    public ImmutableArray<SecretBindingFact> SecretBindings { get; }
}

public sealed record ResolvedBackupSource(
    SourceId SourceId,
    LogicalPath SourceOutputPath,
    ResolvedPhysicalPath PhysicalRoot);

public sealed record DefaultUnitPolicy(
    EffectiveArchiveSpec ArchiveSpec,
    EffectiveHistoryPolicy History);

public abstract record PreparedDeclaredArchiveUnit(
    ArchiveUnitId ArchiveUnitId,
    SourceId SourceId,
    LogicalPath Path,
    EffectiveArchiveSpec ArchiveSpec,
    EffectiveHistoryPolicy History);

public sealed record PreparedUiManagedArchiveUnit(
    ArchiveUnitId ArchiveUnitId,
    SourceId SourceId,
    LogicalPath Path,
    EffectiveArchiveSpec ArchiveSpec,
    EffectiveHistoryPolicy History,
    RuleSet LocalRules)
    : PreparedDeclaredArchiveUnit(ArchiveUnitId, SourceId, Path, ArchiveSpec, History);

public sealed record PreparedFileManagedArchiveUnit(
    ArchiveUnitId ArchiveUnitId,
    SourceId SourceId,
    LogicalPath Path,
    EffectiveArchiveSpec ArchiveSpec,
    EffectiveHistoryPolicy History)
    : PreparedDeclaredArchiveUnit(ArchiveUnitId, SourceId, Path, ArchiveSpec, History);

public sealed record ResolvedExternalSource(
    ExternalSourceId ExternalSourceId,
    PortableExternalSourceKind Kind,
    ArchiveUnitId TargetArchiveUnitId,
    LogicalPath ArchiveDestination,
    ResolvedPhysicalPath PhysicalInput);

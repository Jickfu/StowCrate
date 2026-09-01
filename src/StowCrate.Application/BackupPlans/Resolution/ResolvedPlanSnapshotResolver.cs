using System.Collections.Immutable;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.BackupPlans.Resolution;

public interface IResolvedPlanSnapshotResolver
{
    PlanResolutionResult Resolve(
        PortableBackupPlan plan,
        DevicePlanBindingFacts bindings,
        IReadOnlyCollection<ActivePlanRootFacts> otherActivePlans);
}

public sealed class ResolvedPlanSnapshotResolver : IResolvedPlanSnapshotResolver
{
    public PlanResolutionResult Resolve(
        PortableBackupPlan plan,
        DevicePlanBindingFacts bindings,
        IReadOnlyCollection<ActivePlanRootFacts> otherActivePlans)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(otherActivePlans);

        var issues = new List<PlanResolutionIssue>();
        var semanticErrors = PortableBackupPlanValidator.Validate(plan);
        issues.AddRange(semanticErrors.Select(error => new PlanResolutionIssue(
            PlanResolutionIssueCode.InvalidPortablePlan,
            error.Message,
            error.Location)));

        if (bindings.PlanId != plan.Id)
        {
            issues.Add(new PlanResolutionIssue(
                PlanResolutionIssueCode.BindingPlanMismatch,
                "Device binding facts belong to a different PlanId.",
                bindings.PlanId.Value.ToString("D")));
        }

        var sourceBindings = UniqueBindings(
            bindings.Sources,
            binding => binding.SourceId,
            PlanResolutionIssueCode.DuplicateSourceBinding,
            issues);
        var externalBindings = UniqueBindings(
            bindings.ExternalSources,
            binding => binding.ExternalSourceId,
            PlanResolutionIssueCode.DuplicateExternalSourceBinding,
            issues);
        var secretBindings = UniqueBindings(
            bindings.Secrets,
            binding => binding.SecretSlotId,
            PlanResolutionIssueCode.DuplicateSecretBinding,
            issues);

        var resolvedSources = ImmutableArray.CreateBuilder<ResolvedBackupSource>();
        foreach (var source in plan.Sources)
        {
            if (sourceBindings.TryGetValue(source.Id, out var binding))
            {
                resolvedSources.Add(new ResolvedBackupSource(source.Id, source.SourceOutputPath, binding.PhysicalRoot));
            }
            else
            {
                issues.Add(new PlanResolutionIssue(
                    PlanResolutionIssueCode.MissingSourceBinding,
                    "Source requires an already-resolved physical binding before observation.",
                    source.Id.Value.ToString("D")));
            }
        }

        if (bindings.CurrentRoot is null)
        {
            issues.Add(new PlanResolutionIssue(
                PlanResolutionIssueCode.MissingCurrentRootBinding,
                "CurrentRoot requires an already-resolved physical binding before observation."));
        }

        var resolvedExternals = ImmutableArray.CreateBuilder<ResolvedExternalSource>();
        foreach (var external in plan.ExternalSources)
        {
            if (externalBindings.TryGetValue(external.Id, out var binding))
            {
                resolvedExternals.Add(new ResolvedExternalSource(
                    external.Id,
                    external.Kind,
                    external.TargetArchiveUnitId,
                    external.ArchiveDestination,
                    binding.PhysicalInput));
            }
            else
            {
                issues.Add(new PlanResolutionIssue(
                    PlanResolutionIssueCode.MissingExternalSourceBinding,
                    "External Source requires an already-resolved physical binding before observation.",
                    external.Id.Value.ToString("D")));
            }
        }

        issues.AddRange(DeviceBindingSafetyValidator.Validate(
            plan.Id, bindings.DeviceId, resolvedSources.Select(source => source.PhysicalRoot),
            bindings.CurrentRoot, bindings.HistoryRoot, otherActivePlans));

        if (issues.Count != 0)
        {
            return new PlanResolutionResult(null, issues);
        }

        var defaultPolicy = new DefaultUnitPolicy(
            ArchiveSpecPolicy.Resolve(plan.ArchiveSpecDefault, null),
            HistoryPolicy.Resolve(plan.HistoryDefault, null));
        var preparedUnits = plan.ArchiveUnits.Select(unit => PrepareUnit(plan, unit)).ToArray();
        var declaredSecretBindings = plan.SecretSlots
            .Where(slot => secretBindings.ContainsKey(slot.Id))
            .Select(slot => secretBindings[slot.Id])
            .ToArray();

        var snapshot = new ResolvedPlanSnapshot(
            plan.Id,
            bindings.DeviceId,
            plan.Semantics,
            resolvedSources,
            bindings.CurrentRoot!,
            bindings.HistoryRoot,
            plan.GlobalRules.Rules,
            plan.PlanRules,
            preparedUnits,
            defaultPolicy,
            plan.LinkPolicy,
            plan.ChangeDetection,
            resolvedExternals,
            declaredSecretBindings);
        return new PlanResolutionResult(snapshot, issues);
    }

    private static PreparedDeclaredArchiveUnit PrepareUnit(PortableBackupPlan plan, AuthoredArchiveUnit unit)
    {
        var archiveSpec = ArchiveSpecPolicy.Resolve(plan.ArchiveSpecDefault, unit.ArchiveSpecOverride);
        var history = HistoryPolicy.Resolve(plan.HistoryDefault, unit.HistoryOverride);
        return unit switch
        {
            UiManagedArchiveUnit ui => new PreparedUiManagedArchiveUnit(
                ui.Id,
                ui.SourceId,
                ui.Path,
                archiveSpec,
                history,
                ui.LocalRules),
            FileManagedArchiveUnit file => new PreparedFileManagedArchiveUnit(
                file.Id,
                file.SourceId,
                file.Path,
                archiveSpec,
                history),
            _ => throw new InvalidOperationException($"Unknown Archive Unit {unit.GetType().Name}.")
        };
    }

    private static Dictionary<TKey, TValue> UniqueBindings<TValue, TKey>(
        IEnumerable<TValue> values,
        Func<TValue, TKey> keySelector,
        PlanResolutionIssueCode duplicateCode,
        List<PlanResolutionIssue> issues)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                issues.Add(new PlanResolutionIssue(
                    duplicateCode,
                    $"Multiple binding facts were supplied for '{key}'.",
                    key.ToString()));
            }
        }

        return result;
    }

}

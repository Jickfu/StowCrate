using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.LocalState;

public interface ILocalPhysicalPathResolver
{
    Task<ResolvedPhysicalPath> ResolveAsync(string path, CancellationToken cancellationToken);
}

public sealed record SourceBindingInput(SourceId SourceId, string Path, bool IsActive = true);
public sealed record ExternalBindingInput(ExternalSourceId ExternalSourceId, string Path, bool IsActive = true);
public sealed record LocalBindingSaveRequest(PlanId PlanId, ImmutableArray<SourceBindingInput> Sources,
    string? CurrentRoot, string? HistoryRoot, ImmutableArray<ExternalBindingInput> ExternalSources,
    ImmutableArray<SecretBindingMetadata> ExistingSecretMetadata);
public sealed class LocalBindingValidationException(IReadOnlyList<PlanResolutionIssue> issues) : Exception("Local binding configuration is unsafe or invalid.") { public IReadOnlyList<PlanResolutionIssue> Issues { get; } = issues; }

public sealed class LocalBindingWorkflow(ConfigDatabaseIdentity identity, IDevicePlanBindingStore store, ILocalPhysicalPathResolver paths)
{
    public async Task<DevicePlanLocalBindings> SaveAsync(PortableBackupPlan plan, LocalBindingSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (request.PlanId != plan.Id) throw new ArgumentException("Binding request PlanId differs from portable Plan.", nameof(request));
        var issues = new List<PlanResolutionIssue>();
        ValidateMappings(plan, request, issues);

        var sources = new List<(SourceBindingInput Input, ResolvedPhysicalPath Path)>();
        foreach (var source in request.Sources) sources.Add((source, await paths.ResolveAsync(source.Path, cancellationToken).ConfigureAwait(false)));
        var external = new List<(ExternalBindingInput Input, ResolvedPhysicalPath Path)>();
        foreach (var item in request.ExternalSources) external.Add((item, await paths.ResolveAsync(item.Path, cancellationToken).ConfigureAwait(false)));
        var current = request.CurrentRoot is null ? null : await paths.ResolveAsync(request.CurrentRoot, cancellationToken).ConfigureAwait(false);
        var history = request.HistoryRoot is null ? null : await paths.ResolveAsync(request.HistoryRoot, cancellationToken).ConfigureAwait(false);

        var otherBindings = await store.ListActiveRootFactsAsync(cancellationToken).ConfigureAwait(false);
        var otherFacts = otherBindings.Select(ToRootFacts).ToArray();
        issues.AddRange(DeviceBindingSafetyValidator.Validate(plan.Id, identity.DeviceId, sources.Where(x => x.Input.IsActive).Select(x => x.Path), current, history, otherFacts));
        if (issues.Count != 0) throw new LocalBindingValidationException(issues);

        // 缺少 CurrentRoot 等安全但不完整的绑定仍可持久化，由后续 readiness 报告 PlanNotReady。
        var aggregate = new DevicePlanLocalBindings(plan.Id, identity.DeviceId,
            [.. sources.Select(x => new SourceLocalBinding(x.Input.SourceId, x.Path.CanonicalPath, x.Path.ComparisonKey, x.Input.IsActive))],
            current is null ? null : new(current.CanonicalPath, current.ComparisonKey, true),
            history is null ? null : new(history.CanonicalPath, history.ComparisonKey, true),
            [.. external.Select(x => new ExternalLocalBinding(x.Input.ExternalSourceId, x.Path.CanonicalPath, x.Path.ComparisonKey, x.Input.IsActive))],
            request.ExistingSecretMetadata);
        await store.SaveValidatedAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        return aggregate;
    }

    private static ActivePlanRootFacts ToRootFacts(DevicePlanLocalBindings value) => new(value.PlanId, value.DeviceId,
        value.Sources.Where(x => x.IsActive).Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey)),
        value.CurrentRoot is { IsActive: true } current ? new(current.CanonicalPath, current.ComparisonKey) : null,
        value.HistoryRoot is { IsActive: true } history ? new(history.CanonicalPath, history.ComparisonKey) : null);

    private static void ValidateMappings(PortableBackupPlan plan, LocalBindingSaveRequest request, List<PlanResolutionIssue> issues)
    {
        AddDuplicates(request.Sources.Select(x => x.SourceId), "Source", issues);
        AddDuplicates(request.ExternalSources.Select(x => x.ExternalSourceId), "ExternalSource", issues);
        AddDuplicates(request.ExistingSecretMetadata.Select(x => x.SecretSlotId), "SecretSlot", issues);
        var sourceIds = plan.Sources.Select(x => x.Id).ToHashSet();
        foreach (var source in request.Sources.Where(x => !sourceIds.Contains(x.SourceId))) issues.Add(new(PlanResolutionIssueCode.BindingPlanMismatch, "Source binding references an identity outside the Plan.", source.SourceId.Value.ToString("D")));
        var externalIds = plan.ExternalSources.Select(x => x.Id).ToHashSet();
        foreach (var item in request.ExternalSources.Where(x => !externalIds.Contains(x.ExternalSourceId))) issues.Add(new(PlanResolutionIssueCode.BindingPlanMismatch, "External binding references an identity outside the Plan.", item.ExternalSourceId.Value.ToString("D")));
        var secretIds = plan.SecretSlots.Select(x => x.Id).ToHashSet();
        foreach (var item in request.ExistingSecretMetadata.Where(x => !secretIds.Contains(x.SecretSlotId))) issues.Add(new(PlanResolutionIssueCode.BindingPlanMismatch, "Secret metadata references an identity outside the Plan.", item.SecretSlotId.Value.ToString("D")));
    }

    private static void AddDuplicates<T>(IEnumerable<T> values, string kind, List<PlanResolutionIssue> issues) where T : notnull
    {
        foreach (var duplicate in values.GroupBy(x => x).Where(x => x.Count() > 1)) issues.Add(new(PlanResolutionIssueCode.BindingPlanMismatch, $"Duplicate {kind} binding.", duplicate.Key.ToString()));
    }
}

using System.Collections.Immutable;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.BackupPlans.Resolution;

public readonly record struct DeviceId
{
    public DeviceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("DeviceId must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public sealed record ResolvedPhysicalPath
{
    public ResolvedPhysicalPath(string canonicalPath, string comparisonKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparisonKey);
        if (comparisonKey.Contains('\\') || (comparisonKey.Length > 1 && comparisonKey.EndsWith('/')))
        {
            throw new ArgumentException(
                "Comparison key must use '/' separators and omit a trailing separator except for root.",
                nameof(comparisonKey));
        }

        CanonicalPath = canonicalPath;
        ComparisonKey = comparisonKey;
    }

    public string CanonicalPath { get; }
    public string ComparisonKey { get; }

    public bool Overlaps(ResolvedPhysicalPath other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsSameOrDescendant(ComparisonKey, other.ComparisonKey)
            || IsSameOrDescendant(other.ComparisonKey, ComparisonKey);
    }

    private static bool IsSameOrDescendant(string value, string ancestor) =>
        value.Equals(ancestor, StringComparison.Ordinal)
        || ancestor == "/"
        || value.StartsWith($"{ancestor}/", StringComparison.Ordinal);
}

public readonly record struct SecretRevision
{
    public SecretRevision(long value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "SecretRevision must be positive.");
        }

        Value = value;
    }

    public long Value { get; }
}

public sealed record SourceBindingFact(SourceId SourceId, ResolvedPhysicalPath PhysicalRoot);
public sealed record ExternalSourceBindingFact(ExternalSourceId ExternalSourceId, ResolvedPhysicalPath PhysicalInput);
public sealed record SecretBindingFact(SecretSlotId SecretSlotId, SecretRevision Revision);

public sealed class DevicePlanBindingFacts
{
    public DevicePlanBindingFacts(
        PlanId planId,
        DeviceId deviceId,
        IEnumerable<SourceBindingFact> sources,
        ResolvedPhysicalPath? currentRoot,
        ResolvedPhysicalPath? historyRoot,
        IEnumerable<ExternalSourceBindingFact> externalSources,
        IEnumerable<SecretBindingFact> secrets)
    {
        PlanId = planId;
        DeviceId = deviceId;
        Sources = [.. sources];
        CurrentRoot = currentRoot;
        HistoryRoot = historyRoot;
        ExternalSources = [.. externalSources];
        Secrets = [.. secrets];
    }

    public PlanId PlanId { get; }
    public DeviceId DeviceId { get; }
    public ImmutableArray<SourceBindingFact> Sources { get; }
    public ResolvedPhysicalPath? CurrentRoot { get; }
    public ResolvedPhysicalPath? HistoryRoot { get; }
    public ImmutableArray<ExternalSourceBindingFact> ExternalSources { get; }
    public ImmutableArray<SecretBindingFact> Secrets { get; }
}

public sealed class ActivePlanRootFacts
{
    public ActivePlanRootFacts(
        PlanId planId,
        DeviceId deviceId,
        IEnumerable<ResolvedPhysicalPath> sourceRoots,
        ResolvedPhysicalPath? currentRoot,
        ResolvedPhysicalPath? historyRoot)
    {
        PlanId = planId;
        DeviceId = deviceId;
        SourceRoots = [.. sourceRoots];
        CurrentRoot = currentRoot;
        HistoryRoot = historyRoot;
    }

    public PlanId PlanId { get; }
    public DeviceId DeviceId { get; }
    public ImmutableArray<ResolvedPhysicalPath> SourceRoots { get; }
    public ResolvedPhysicalPath? CurrentRoot { get; }
    public ResolvedPhysicalPath? HistoryRoot { get; }
}

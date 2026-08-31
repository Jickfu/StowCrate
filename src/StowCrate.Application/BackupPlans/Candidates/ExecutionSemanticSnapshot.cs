using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.BackupPlans.Candidates;

public readonly record struct PlanRevision
{
    public PlanRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        Value = value;
    }
    public long Value { get; }
}

public sealed record UnitExecutionSemanticState(
    ArchiveUnitId ArchiveUnitId,
    ExecutionSemanticFingerprint ExecutionSemantic,
    ExecutionBindingFingerprint ExecutionBinding,
    Sha256Digest? FileManagedRuleSource,
    SecureRevisionRequirement? SecureRequirement,
    HistoryMaintenanceFingerprint HistoryMaintenance);

public sealed class ExecutionSemanticSnapshot
{
    public ExecutionSemanticSnapshot(
        PlanId planId,
        DeviceId deviceId,
        PlanRevision? managedPlanRevision,
        PlanSemanticFingerprint planSemantic,
        IEnumerable<UnitExecutionSemanticState> units)
    {
        PlanId = planId;
        DeviceId = deviceId;
        ManagedPlanRevision = managedPlanRevision;
        PlanSemantic = planSemantic;
        Units = units.ToImmutableDictionary(unit => unit.ArchiveUnitId);
    }

    public PlanId PlanId { get; }
    public DeviceId DeviceId { get; }
    public PlanRevision? ManagedPlanRevision { get; }
    public PlanSemanticFingerprint PlanSemantic { get; }
    public ImmutableDictionary<ArchiveUnitId, UnitExecutionSemanticState> Units { get; }
}

public enum PublishStaleReason
{
    PlanOrDeviceMismatch,
    UnitMissing,
    ExecutionSemanticDrift,
    ExecutionBindingDrift,
    RuleSourceDrift,
    SecretRevisionDrift
}

public sealed record PublishRevalidationResult(
    bool CanPublish,
    bool SkipRetentionCleanup,
    bool HistoryMaintenanceOutOfSync,
    ImmutableArray<PublishStaleReason> Reasons);

public static class PublishTimeRevalidator
{
    public static PublishRevalidationResult Revalidate(
        ExecutionSemanticSnapshot captured,
        ExecutionSemanticSnapshot current,
        ArchiveUnitId archiveUnitId)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(current);
        var reasons = new List<PublishStaleReason>();
        if (captured.PlanId != current.PlanId || captured.DeviceId != current.DeviceId)
            reasons.Add(PublishStaleReason.PlanOrDeviceMismatch);
        if (!captured.Units.TryGetValue(archiveUnitId, out var before) || !current.Units.TryGetValue(archiveUnitId, out var after))
            reasons.Add(PublishStaleReason.UnitMissing);
        else
        {
            // PlanRevision/PlanSemantic 只触发重读；最终是否阻止由当前 unit 的 effective/local facts 决定。
            if (before.ExecutionSemantic != after.ExecutionSemantic) reasons.Add(PublishStaleReason.ExecutionSemanticDrift);
            if (before.ExecutionBinding != after.ExecutionBinding) reasons.Add(PublishStaleReason.ExecutionBindingDrift);
            if (before.FileManagedRuleSource != after.FileManagedRuleSource) reasons.Add(PublishStaleReason.RuleSourceDrift);
            if (before.SecureRequirement != after.SecureRequirement) reasons.Add(PublishStaleReason.SecretRevisionDrift);
            var maintenanceDrift = before.HistoryMaintenance != after.HistoryMaintenance;
            return new PublishRevalidationResult(
                reasons.Count == 0,
                maintenanceDrift,
                maintenanceDrift,
                [.. reasons]);
        }
        return new PublishRevalidationResult(false, false, false, [.. reasons]);
    }
}

public static class HistoryMaintenanceFingerprintCalculator
{
    public static HistoryMaintenanceFingerprint Compute(EffectiveHistoryPolicy history) => new(
        CanonicalFingerprintEncodingV1.Encode("history-maintenance", writer =>
        {
            switch (history)
            {
                case EffectiveHistoryDisabled: writer.SignedNumber(1, 0); break;
                case EffectiveHistoryEnabled enabled:
                    writer.SignedNumber(1, 1);
                    if (enabled.Retention is KeepAllRetention) writer.SignedNumber(2, 0);
                    else if (enabled.Retention is KeepLastVersionsRetention keep) { writer.SignedNumber(2, 1); writer.SignedNumber(3, keep.Count); }
                    break;
            }
        }));
}

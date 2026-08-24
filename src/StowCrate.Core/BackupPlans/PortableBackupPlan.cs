using System.Collections.Immutable;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.BackupPlans;

public readonly record struct PlanId
{
    public PlanId(Guid value) => Value = PortableId.Validate(value, nameof(value));
    public Guid Value { get; }
}

public readonly record struct SourceId
{
    public SourceId(Guid value) => Value = PortableId.Validate(value, nameof(value));
    public Guid Value { get; }
}

public readonly record struct ArchiveUnitId
{
    public ArchiveUnitId(Guid value) => Value = PortableId.Validate(value, nameof(value));
    public Guid Value { get; }
}

public readonly record struct ExternalSourceId
{
    public ExternalSourceId(Guid value) => Value = PortableId.Validate(value, nameof(value));
    public Guid Value { get; }
}

public readonly record struct SecretSlotId
{
    public SecretSlotId(Guid value) => Value = PortableId.Validate(value, nameof(value));
    public Guid Value { get; }
}

internal static class PortableId
{
    public static Guid Validate(Guid value, string parameterName)
    {
        var canonical = value.ToString("D");
        if (canonical[14] != '4' || canonical[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw new ArgumentException("Portable identity must be a UUID v4.", parameterName);
        }

        return value;
    }
}

public sealed record PortableSemanticsPins(int Rules, int Archive, int OutputPathEncoding);

public sealed class PortableBackupPlan
{
    public PortableBackupPlan(
        PlanId id,
        string name,
        string? description,
        PortableSemanticsPins semantics,
        IEnumerable<PortableBackupSource> sources,
        GlobalRulesSnapshot globalRules,
        IEnumerable<BackupRule> planRules,
        AuthoredArchiveSpec archiveSpecDefault,
        IEnumerable<AuthoredArchiveUnit> archiveUnits,
        IEnumerable<PortableSecretSlot> secretSlots,
        PortableLinkPolicy linkPolicy,
        PortableChangeDetectionMode changeDetection,
        AuthoredHistoryPolicy historyDefault,
        PortableScheduleIntent schedule,
        IEnumerable<PortableExternalSource> externalSources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
        Description = description;
        Semantics = semantics;
        Sources = [.. sources];
        GlobalRules = globalRules;
        PlanRules = [.. planRules];
        ArchiveSpecDefault = archiveSpecDefault;
        ArchiveUnits = [.. archiveUnits];
        SecretSlots = [.. secretSlots];
        LinkPolicy = linkPolicy;
        ChangeDetection = changeDetection;
        HistoryDefault = historyDefault;
        Schedule = schedule;
        ExternalSources = [.. externalSources];
    }

    public PlanId Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public PortableSemanticsPins Semantics { get; }
    public ImmutableArray<PortableBackupSource> Sources { get; }
    public GlobalRulesSnapshot GlobalRules { get; }
    public ImmutableArray<BackupRule> PlanRules { get; }
    public AuthoredArchiveSpec ArchiveSpecDefault { get; }
    public ImmutableArray<AuthoredArchiveUnit> ArchiveUnits { get; }
    public ImmutableArray<PortableSecretSlot> SecretSlots { get; }
    public PortableLinkPolicy LinkPolicy { get; }
    public PortableChangeDetectionMode ChangeDetection { get; }
    public AuthoredHistoryPolicy HistoryDefault { get; }
    public PortableScheduleIntent Schedule { get; }
    public ImmutableArray<PortableExternalSource> ExternalSources { get; }
}

public sealed record PortableBackupSource(SourceId Id, string Name, LogicalPath SourceOutputPath);

public sealed class GlobalRulesSnapshot
{
    public GlobalRulesSnapshot(IEnumerable<BackupRule> rules, GlobalRuleProvenance? provenance)
    {
        Rules = [.. rules];
        Provenance = provenance;
    }

    public ImmutableArray<BackupRule> Rules { get; }
    public GlobalRuleProvenance? Provenance { get; }
}

public sealed record GlobalRuleProvenance(string? Id, string? Name, string? Revision);

public abstract record AuthoredArchiveUnit(
    ArchiveUnitId Id,
    SourceId SourceId,
    LogicalPath Path,
    AuthoredArchiveSpecOverride? ArchiveSpecOverride,
    AuthoredHistoryOverride? HistoryOverride);

public sealed record UiManagedArchiveUnit(
    ArchiveUnitId Id,
    SourceId SourceId,
    LogicalPath Path,
    RuleSet LocalRules,
    AuthoredArchiveSpecOverride? ArchiveSpecOverride,
    AuthoredHistoryOverride? HistoryOverride)
    : AuthoredArchiveUnit(Id, SourceId, Path, ArchiveSpecOverride, HistoryOverride);

public sealed record FileManagedArchiveUnit(
    ArchiveUnitId Id,
    SourceId SourceId,
    LogicalPath Path,
    AuthoredArchiveSpecOverride? ArchiveSpecOverride,
    AuthoredHistoryOverride? HistoryOverride)
    : AuthoredArchiveUnit(Id, SourceId, Path, ArchiveSpecOverride, HistoryOverride);

public sealed record AuthoredArchiveSpec(
    PortableArchiveFormat Format,
    PortableCompressionPreset CompressionPreset,
    AuthoredProtection Protection);

public sealed record AuthoredArchiveSpecOverride(
    PortableArchiveFormat? Format,
    PortableCompressionPreset? CompressionPreset,
    AuthoredProtection? Protection);

public abstract record AuthoredProtection;
public sealed record NoProtection : AuthoredProtection;
public sealed record PrivacyProtection : AuthoredProtection;
public sealed record SecureProtection(SecretSlotId SecretSlotId) : AuthoredProtection;

public sealed record PortableSecretSlot(SecretSlotId Id, string Name);

public abstract record AuthoredHistoryPolicy;
public sealed record HistoryDisabled : AuthoredHistoryPolicy;
public sealed record HistoryEnabled(AuthoredRetentionPolicy Retention) : AuthoredHistoryPolicy;

public abstract record AuthoredHistoryOverride;
public sealed record HistoryInherit : AuthoredHistoryOverride;
public sealed record HistoryOverrideDisabled : AuthoredHistoryOverride;
public sealed record HistoryOverrideEnabled(AuthoredRetentionPolicy Retention) : AuthoredHistoryOverride;

public abstract record AuthoredRetentionPolicy;
public sealed record KeepAllRetention : AuthoredRetentionPolicy;
public sealed record KeepLastVersionsRetention(int Count) : AuthoredRetentionPolicy;

public abstract record PortableScheduleIntent;
public sealed record ManualOnlySchedule : PortableScheduleIntent;
public sealed record AutomaticSchedule : PortableScheduleIntent
{
    public AutomaticSchedule(IEnumerable<PortableScheduleTrigger> triggers, PortableMissedRunPolicy missedRunPolicy)
    {
        Triggers = [.. triggers];
        MissedRunPolicy = missedRunPolicy;
    }

    public ImmutableArray<PortableScheduleTrigger> Triggers { get; }
    public PortableMissedRunPolicy MissedRunPolicy { get; }
}

public abstract record PortableScheduleTrigger;
public sealed record DailyScheduleTrigger(TimeOnly LocalTime) : PortableScheduleTrigger;
public sealed record WeeklyScheduleTrigger(ImmutableArray<DayOfWeek> DaysOfWeek, TimeOnly LocalTime) : PortableScheduleTrigger
{
    public WeeklyScheduleTrigger(IEnumerable<DayOfWeek> daysOfWeek, TimeOnly localTime)
        : this([.. daysOfWeek], localTime)
    {
    }
}
public sealed record OnStartupScheduleTrigger : PortableScheduleTrigger;

public sealed record PortableExternalSource(
    ExternalSourceId Id,
    string Name,
    PortableExternalSourceKind Kind,
    ArchiveUnitId TargetArchiveUnitId,
    LogicalPath ArchiveDestination);

public enum PortableArchiveFormat { SevenZip, Zip, TarZstd }
public enum PortableCompressionPreset { Store, Fast, Standard, Extreme }
public enum PortableLinkPolicy { Preserve, Skip }
public enum PortableChangeDetectionMode { Standard, Strict }
public enum PortableMissedRunPolicy { Skip, RunOnceWhenAvailable }
public enum PortableExternalSourceKind { File, Directory }

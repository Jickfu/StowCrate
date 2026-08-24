using System.Collections.Immutable;
using System.Globalization;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Rules;

namespace StowCrate.Infrastructure.Configuration.BackupPlans.V1;

public static class BackupPlanDocumentV1Projector
{
    public static BackupPlanDocumentV1 Project(PortableBackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var errors = PortableBackupPlanValidator.Validate(plan);
        if (!errors.IsEmpty)
        {
            throw new BackupPlanDocumentProjectionException(errors);
        }

        return new BackupPlanDocumentV1
        {
            SchemaVersion = 1,
            PlanId = plan.Id.Value,
            Name = plan.Name,
            Description = plan.Description,
            Semantics = new PortableSemanticsPinsV1(
                plan.Semantics.Rules,
                plan.Semantics.Archive,
                plan.Semantics.OutputPathEncoding),
            Sources = plan.Sources
                .OrderBy(source => IdText(source.Id.Value), StringComparer.Ordinal)
                .Select(MapSource)
                .ToArray(),
            GlobalRules = MapGlobalRules(plan.GlobalRules),
            PlanRules = plan.PlanRules.Select(MapRule).ToArray(),
            ArchiveSpecDefault = MapArchiveSpec(plan.ArchiveSpecDefault),
            ArchiveUnits = plan.ArchiveUnits
                .OrderBy(unit => IdText(unit.SourceId.Value), StringComparer.Ordinal)
                .ThenBy(unit => unit.Path.Value, StringComparer.Ordinal)
                .ThenBy(unit => IdText(unit.Id.Value), StringComparer.Ordinal)
                .Select(MapArchiveUnit)
                .ToArray(),
            SecretSlots = plan.SecretSlots
                .OrderBy(slot => IdText(slot.Id.Value), StringComparer.Ordinal)
                .Select(slot => new SecretSlotV1(slot.Id.Value, slot.Name, SecretPurposeV1.ArchiveEncryption))
                .ToArray(),
            LinkPolicy = plan.LinkPolicy is PortableLinkPolicy.Preserve ? LinkPolicyV1.Preserve : LinkPolicyV1.Skip,
            ChangeDetection = new ChangeDetectionV1(
                plan.ChangeDetection is PortableChangeDetectionMode.Standard
                    ? ChangeDetectionModeV1.Standard
                    : ChangeDetectionModeV1.Strict),
            HistoryDefault = MapHistoryPolicy(plan.HistoryDefault),
            Schedule = MapSchedule(plan.Schedule),
            ExternalSources = plan.ExternalSources
                .OrderBy(external => IdText(external.TargetArchiveUnitId.Value), StringComparer.Ordinal)
                .ThenBy(external => external.ArchiveDestination.Value, StringComparer.Ordinal)
                .ThenBy(external => ExternalKindText(external.Kind), StringComparer.Ordinal)
                .ThenBy(external => IdText(external.Id.Value), StringComparer.Ordinal)
                .Select(MapExternalSource)
                .ToArray()
        };
    }

    private static BackupSourceV1 MapSource(PortableBackupSource source) =>
        new(source.Id.Value, source.Name, source.SourceOutputPath.Value);

    private static GlobalRulesSnapshotV1 MapGlobalRules(GlobalRulesSnapshot snapshot) =>
        new(
            snapshot.Rules.Select(MapRule).ToArray(),
            snapshot.Provenance is null
                ? null
                : new GlobalRuleProvenanceV1(
                    snapshot.Provenance.Id,
                    snapshot.Provenance.Name,
                    snapshot.Provenance.Revision));

    private static RuleV1 MapRule(BackupRule rule) =>
        new(rule.Action is RuleAction.Include ? RuleActionV1.Include : RuleActionV1.Exclude, rule.Pattern);

    private static ArchiveUnitDeclarationV1 MapArchiveUnit(AuthoredArchiveUnit unit) => unit switch
    {
        UiManagedArchiveUnit ui => new UiManagedArchiveUnitV1
        {
            ArchiveUnitId = ui.Id.Value,
            SourceId = ui.SourceId.Value,
            Path = ui.Path.Value,
            RuleSource = RuleSourceV1.UiManaged,
            LocalRules = new UiManagedLocalRulesV1(
                ui.LocalRules.Mode is RuleMode.IncludeOnly ? RuleModeV1.IncludeOnly : RuleModeV1.Exclude,
                MapCasePolicy(ui.LocalRules.CaseSensitivity),
                ui.LocalRules.Rules.Select(MapRule).ToArray()),
            ArchiveSpecOverride = MapArchiveSpecOverride(ui.ArchiveSpecOverride),
            HistoryOverride = MapHistoryOverride(ui.HistoryOverride)
        },
        FileManagedArchiveUnit file => new FileManagedArchiveUnitV1
        {
            ArchiveUnitId = file.Id.Value,
            SourceId = file.SourceId.Value,
            Path = file.Path.Value,
            RuleSource = RuleSourceV1.FileManaged,
            ArchiveSpecOverride = MapArchiveSpecOverride(file.ArchiveSpecOverride),
            HistoryOverride = MapHistoryOverride(file.HistoryOverride)
        },
        _ => throw new InvalidOperationException($"Unknown authored Archive Unit {unit.GetType().Name}.")
    };

    private static ArchiveSpecV1 MapArchiveSpec(AuthoredArchiveSpec spec) =>
        new(MapArchiveFormat(spec.Format), MapCompressionPreset(spec.CompressionPreset), MapProtection(spec.Protection));

    private static ArchiveSpecOverrideV1? MapArchiveSpecOverride(AuthoredArchiveSpecOverride? spec) => spec is null
        ? null
        : new ArchiveSpecOverrideV1(
            spec.Format is null ? null : MapArchiveFormat(spec.Format.Value),
            spec.CompressionPreset is null ? null : MapCompressionPreset(spec.CompressionPreset.Value),
            spec.Protection is null ? null : MapProtection(spec.Protection));

    private static ProtectionConfigurationV1 MapProtection(AuthoredProtection protection) => protection switch
    {
        NoProtection => new NoProtectionV1(ProtectionModeV1.None),
        PrivacyProtection => new PrivacyProtectionV1(ProtectionModeV1.Privacy),
        SecureProtection secure => new SecureProtectionV1(ProtectionModeV1.Secure, secure.SecretSlotId.Value),
        _ => throw new InvalidOperationException($"Unknown authored protection {protection.GetType().Name}.")
    };

    private static HistoryPolicyV1 MapHistoryPolicy(AuthoredHistoryPolicy history) => history switch
    {
        HistoryDisabled => new HistoryDisabledV1(HistoryModeV1.Disabled),
        HistoryEnabled enabled => new HistoryEnabledV1(HistoryModeV1.Enabled, MapRetention(enabled.Retention)),
        _ => throw new InvalidOperationException($"Unknown authored history {history.GetType().Name}.")
    };

    private static HistoryOverrideV1? MapHistoryOverride(AuthoredHistoryOverride? history) => history switch
    {
        null => null,
        HistoryInherit => new HistoryInheritV1(HistoryOverrideModeV1.Inherit),
        HistoryOverrideDisabled => new HistoryOverrideDisabledV1(HistoryOverrideModeV1.Disabled),
        HistoryOverrideEnabled enabled => new HistoryOverrideEnabledV1(
            HistoryOverrideModeV1.Enabled,
            MapRetention(enabled.Retention)),
        _ => throw new InvalidOperationException($"Unknown authored history override {history.GetType().Name}.")
    };

    private static RetentionPolicyV1 MapRetention(AuthoredRetentionPolicy retention) => retention switch
    {
        KeepAllRetention => new KeepAllRetentionV1(RetentionKindV1.KeepAll),
        KeepLastVersionsRetention keepLast => new KeepLastVersionsRetentionV1(
            RetentionKindV1.KeepLastVersions,
            keepLast.Count),
        _ => throw new InvalidOperationException($"Unknown authored retention {retention.GetType().Name}.")
    };

    private static ScheduleIntentV1 MapSchedule(PortableScheduleIntent schedule) => schedule switch
    {
        ManualOnlySchedule => new ManualOnlyScheduleV1(false),
        AutomaticSchedule automatic => new AutomaticScheduleV1(
            true,
            automatic.Triggers
                .OrderBy(TriggerTypeText, StringComparer.Ordinal)
                .ThenBy(TriggerTimeText, StringComparer.Ordinal)
                .ThenBy(TriggerDaysText, StringComparer.Ordinal)
                .Select(MapTrigger)
                .ToArray(),
            automatic.MissedRunPolicy is PortableMissedRunPolicy.Skip
                ? MissedRunPolicyV1.Skip
                : MissedRunPolicyV1.RunOnceWhenAvailable),
        _ => throw new InvalidOperationException($"Unknown authored schedule {schedule.GetType().Name}.")
    };

    private static ScheduleTriggerV1 MapTrigger(PortableScheduleTrigger trigger) => trigger switch
    {
        DailyScheduleTrigger daily => new DailyTriggerV1(
            ScheduleTriggerTypeV1.Daily,
            FormatTime(daily.LocalTime)),
        WeeklyScheduleTrigger weekly => new WeeklyTriggerV1(
            ScheduleTriggerTypeV1.Weekly,
            weekly.DaysOfWeek.OrderBy(WeekdayOrder).Select(MapDayOfWeek).ToArray(),
            FormatTime(weekly.LocalTime)),
        OnStartupScheduleTrigger => new OnStartupTriggerV1(ScheduleTriggerTypeV1.OnStartup),
        _ => throw new InvalidOperationException($"Unknown authored trigger {trigger.GetType().Name}.")
    };

    private static ExternalSourceDeclarationV1 MapExternalSource(PortableExternalSource external) =>
        new(
            external.Id.Value,
            external.Name,
            external.Kind is PortableExternalSourceKind.File ? ExternalSourceKindV1.File : ExternalSourceKindV1.Directory,
            external.TargetArchiveUnitId.Value,
            external.ArchiveDestination.Value);

    private static string TriggerTypeText(PortableScheduleTrigger trigger) => trigger switch
    {
        DailyScheduleTrigger => "daily",
        OnStartupScheduleTrigger => "onStartup",
        WeeklyScheduleTrigger => "weekly",
        _ => throw new InvalidOperationException($"Unknown authored trigger {trigger.GetType().Name}.")
    };

    private static string TriggerTimeText(PortableScheduleTrigger trigger) => trigger switch
    {
        DailyScheduleTrigger daily => FormatTime(daily.LocalTime),
        WeeklyScheduleTrigger weekly => FormatTime(weekly.LocalTime),
        OnStartupScheduleTrigger => string.Empty,
        _ => throw new InvalidOperationException($"Unknown authored trigger {trigger.GetType().Name}.")
    };

    private static string TriggerDaysText(PortableScheduleTrigger trigger) => trigger is WeeklyScheduleTrigger weekly
        ? string.Join(',', weekly.DaysOfWeek.OrderBy(WeekdayOrder).Select(WeekdayOrder))
        : string.Empty;

    private static int WeekdayOrder(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 0,
        DayOfWeek.Tuesday => 1,
        DayOfWeek.Wednesday => 2,
        DayOfWeek.Thursday => 3,
        DayOfWeek.Friday => 4,
        DayOfWeek.Saturday => 5,
        DayOfWeek.Sunday => 6,
        _ => throw new InvalidOperationException($"Unknown weekday {day}.")
    };

    private static DayOfWeekV1 MapDayOfWeek(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DayOfWeekV1.Monday,
        DayOfWeek.Tuesday => DayOfWeekV1.Tuesday,
        DayOfWeek.Wednesday => DayOfWeekV1.Wednesday,
        DayOfWeek.Thursday => DayOfWeekV1.Thursday,
        DayOfWeek.Friday => DayOfWeekV1.Friday,
        DayOfWeek.Saturday => DayOfWeekV1.Saturday,
        DayOfWeek.Sunday => DayOfWeekV1.Sunday,
        _ => throw new InvalidOperationException($"Unknown weekday {day}.")
    };

    private static CasePolicyV1 MapCasePolicy(CaseSensitivity sensitivity) => sensitivity switch
    {
        CaseSensitivity.Auto => CasePolicyV1.Auto,
        CaseSensitivity.Sensitive => CasePolicyV1.Sensitive,
        CaseSensitivity.Insensitive => CasePolicyV1.Insensitive,
        _ => throw new InvalidOperationException($"Unknown case sensitivity {sensitivity}.")
    };

    private static ArchiveFormatV1 MapArchiveFormat(PortableArchiveFormat format) => format switch
    {
        PortableArchiveFormat.SevenZip => ArchiveFormatV1.SevenZip,
        PortableArchiveFormat.Zip => ArchiveFormatV1.Zip,
        PortableArchiveFormat.TarZstd => ArchiveFormatV1.TarZstd,
        _ => throw new InvalidOperationException($"Unknown archive format {format}.")
    };

    private static CompressionPresetV1 MapCompressionPreset(PortableCompressionPreset preset) => preset switch
    {
        PortableCompressionPreset.Store => CompressionPresetV1.Store,
        PortableCompressionPreset.Fast => CompressionPresetV1.Fast,
        PortableCompressionPreset.Standard => CompressionPresetV1.Standard,
        PortableCompressionPreset.Extreme => CompressionPresetV1.Extreme,
        _ => throw new InvalidOperationException($"Unknown compression preset {preset}.")
    };

    private static string ExternalKindText(PortableExternalSourceKind kind) =>
        kind is PortableExternalSourceKind.Directory ? "directory" : "file";

    private static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
    private static string IdText(Guid value) => value.ToString("D");
}

public sealed class BackupPlanDocumentProjectionException(
    ImmutableArray<BackupPlanSemanticError> errors)
    : Exception("Portable Backup Plan is not semantically valid for v1 document projection.")
{
    public ImmutableArray<BackupPlanSemanticError> Errors { get; } = errors;
}

using System.Globalization;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Infrastructure.Configuration.BackupPlans.V1;

public static class BackupPlanDocumentV1Mapper
{
    public static BackupPlanSemanticResult Map(BackupPlanDocumentV1 document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var pins = new PortableSemanticsPins(
            document.Semantics.Rules,
            document.Semantics.Archive,
            document.Semantics.OutputPathEncoding);
        var unsupported = PortableSemanticsSupport.Validate(pins);
        if (!unsupported.IsEmpty)
        {
            return new BackupPlanSemanticResult(null, unsupported);
        }

        var errors = new List<BackupPlanSemanticError>();
        try
        {
            var plan = new PortableBackupPlan(
                new PlanId(document.PlanId),
                document.Name,
                document.Description,
                pins,
                document.Sources.Select(MapSource),
                MapGlobalRules(document.GlobalRules, errors),
                MapRules(document.PlanRules, "/planRules", errors),
                MapArchiveSpec(document.ArchiveSpecDefault),
                document.ArchiveUnits.Select((unit, index) => MapArchiveUnit(unit, index, errors)),
                document.SecretSlots.Select(slot => new PortableSecretSlot(new SecretSlotId(slot.SecretSlotId), slot.Name)),
                MapLinkPolicy(document.LinkPolicy),
                MapChangeDetection(document.ChangeDetection.Mode),
                MapHistoryPolicy(document.HistoryDefault),
                MapSchedule(document.Schedule),
                document.ExternalSources.Select(MapExternalSource));

            errors.AddRange(PortableBackupPlanValidator.Validate(plan));
            return errors.Count == 0
                ? new BackupPlanSemanticResult(plan, errors)
                : new BackupPlanSemanticResult(null, errors);
        }
        catch (ArgumentException exception)
        {
            errors.Add(new BackupPlanSemanticError(
                BackupPlanSemanticErrorCode.InvalidValue,
                exception.Message));
            return new BackupPlanSemanticResult(null, errors);
        }
    }

    private static PortableBackupSource MapSource(BackupSourceV1 source) =>
        new(new SourceId(source.SourceId), source.Name, new LogicalPath(source.SourceOutputPath));

    private static GlobalRulesSnapshot MapGlobalRules(
        GlobalRulesSnapshotV1 snapshot,
        ICollection<BackupPlanSemanticError> errors) =>
        new(
            MapRules(snapshot.Rules, "/globalRules/rules", errors),
            snapshot.Provenance is null
                ? null
                : new GlobalRuleProvenance(snapshot.Provenance.Id, snapshot.Provenance.Name, snapshot.Provenance.Revision));

    private static List<BackupRule> MapRules(
        IReadOnlyList<RuleV1> rules,
        string location,
        ICollection<BackupPlanSemanticError> errors)
    {
        var mapped = new List<BackupRule>(rules.Count);
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            try
            {
                mapped.Add(new BackupRule(MapRuleAction(rule.Action), rule.Pattern));
            }
            catch (ArgumentException exception)
            {
                errors.Add(new BackupPlanSemanticError(
                    BackupPlanSemanticErrorCode.InvalidRulePattern,
                    exception.Message,
                    $"{location}/{index}/pattern"));
            }
        }

        return mapped;
    }

    private static AuthoredArchiveUnit MapArchiveUnit(
        ArchiveUnitDeclarationV1 unit,
        int index,
        ICollection<BackupPlanSemanticError> errors) => unit switch
        {
            UiManagedArchiveUnitV1 ui => new UiManagedArchiveUnit(
                new ArchiveUnitId(ui.ArchiveUnitId),
                new SourceId(ui.SourceId),
                new LogicalPath(ui.Path),
                new RuleSet(
                    MapRuleMode(ui.LocalRules.Mode),
                    MapCaseSensitivity(ui.LocalRules.Case),
                    MapRules(ui.LocalRules.Rules, $"/archiveUnits/{index}/localRules/rules", errors)),
                MapArchiveSpecOverride(ui.ArchiveSpecOverride),
                MapHistoryOverride(ui.HistoryOverride)),
            FileManagedArchiveUnitV1 file => new FileManagedArchiveUnit(
                new ArchiveUnitId(file.ArchiveUnitId),
                new SourceId(file.SourceId),
                new LogicalPath(file.Path),
                MapArchiveSpecOverride(file.ArchiveSpecOverride),
                MapHistoryOverride(file.HistoryOverride)),
            _ => throw new InvalidOperationException($"Unknown Archive Unit DTO {unit.GetType().Name}.")
        };

    private static AuthoredArchiveSpec MapArchiveSpec(ArchiveSpecV1 spec) =>
        new(MapArchiveFormat(spec.Format), MapCompressionPreset(spec.CompressionPreset), MapProtection(spec.Protection));

    private static AuthoredArchiveSpecOverride? MapArchiveSpecOverride(ArchiveSpecOverrideV1? spec) => spec is null
        ? null
        : new AuthoredArchiveSpecOverride(
            spec.Format is null ? null : MapArchiveFormat(spec.Format.Value),
            spec.CompressionPreset is null ? null : MapCompressionPreset(spec.CompressionPreset.Value),
            spec.Protection is null ? null : MapProtection(spec.Protection));

    private static AuthoredProtection MapProtection(ProtectionConfigurationV1 protection) => protection switch
    {
        NoProtectionV1 => new NoProtection(),
        PrivacyProtectionV1 => new PrivacyProtection(),
        SecureProtectionV1 secure => new SecureProtection(new SecretSlotId(secure.SecretSlotId)),
        _ => throw new InvalidOperationException($"Unknown protection DTO {protection.GetType().Name}.")
    };

    private static AuthoredHistoryPolicy MapHistoryPolicy(HistoryPolicyV1 history) => history switch
    {
        HistoryDisabledV1 => new HistoryDisabled(),
        HistoryEnabledV1 enabled => new HistoryEnabled(MapRetention(enabled.Retention)),
        _ => throw new InvalidOperationException($"Unknown history DTO {history.GetType().Name}.")
    };

    private static AuthoredHistoryOverride? MapHistoryOverride(HistoryOverrideV1? history) => history switch
    {
        null => null,
        HistoryInheritV1 => new HistoryInherit(),
        HistoryOverrideDisabledV1 => new HistoryOverrideDisabled(),
        HistoryOverrideEnabledV1 enabled => new HistoryOverrideEnabled(MapRetention(enabled.Retention)),
        _ => throw new InvalidOperationException($"Unknown history override DTO {history.GetType().Name}.")
    };

    private static AuthoredRetentionPolicy MapRetention(RetentionPolicyV1 retention) => retention switch
    {
        KeepAllRetentionV1 => new KeepAllRetention(),
        KeepLastVersionsRetentionV1 keepLast => new KeepLastVersionsRetention(keepLast.Count),
        _ => throw new InvalidOperationException($"Unknown retention DTO {retention.GetType().Name}.")
    };

    private static PortableScheduleIntent MapSchedule(ScheduleIntentV1 schedule) => schedule switch
    {
        ManualOnlyScheduleV1 => new ManualOnlySchedule(),
        AutomaticScheduleV1 automatic => new AutomaticSchedule(
            automatic.Triggers.Select(MapTrigger),
            automatic.MissedRunPolicy switch
            {
                MissedRunPolicyV1.Skip => PortableMissedRunPolicy.Skip,
                MissedRunPolicyV1.RunOnceWhenAvailable => PortableMissedRunPolicy.RunOnceWhenAvailable,
                _ => throw new InvalidOperationException($"Unknown missed-run policy {automatic.MissedRunPolicy}.")
            }),
        _ => throw new InvalidOperationException($"Unknown schedule DTO {schedule.GetType().Name}.")
    };

    private static PortableScheduleTrigger MapTrigger(ScheduleTriggerV1 trigger) => trigger switch
    {
        DailyTriggerV1 daily => new DailyScheduleTrigger(ParseTime(daily.LocalTime)),
        WeeklyTriggerV1 weekly => new WeeklyScheduleTrigger(weekly.DaysOfWeek.Select(MapDayOfWeek), ParseTime(weekly.LocalTime)),
        OnStartupTriggerV1 => new OnStartupScheduleTrigger(),
        _ => throw new InvalidOperationException($"Unknown trigger DTO {trigger.GetType().Name}.")
    };

    private static PortableExternalSource MapExternalSource(ExternalSourceDeclarationV1 external) =>
        new(
            new ExternalSourceId(external.ExternalSourceId),
            external.Name,
            external.Kind is ExternalSourceKindV1.File ? PortableExternalSourceKind.File : PortableExternalSourceKind.Directory,
            new ArchiveUnitId(external.TargetArchiveUnitId),
            new LogicalPath(external.ArchiveDestination));

    private static TimeOnly ParseTime(string value) =>
        TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);

    private static RuleAction MapRuleAction(RuleActionV1 action) =>
        action is RuleActionV1.Include ? RuleAction.Include : RuleAction.Exclude;

    private static RuleMode MapRuleMode(RuleModeV1 mode) =>
        mode is RuleModeV1.IncludeOnly ? RuleMode.IncludeOnly : RuleMode.Exclude;

    private static CaseSensitivity MapCaseSensitivity(CasePolicyV1 policy) => policy switch
    {
        CasePolicyV1.Auto => CaseSensitivity.Auto,
        CasePolicyV1.Sensitive => CaseSensitivity.Sensitive,
        CasePolicyV1.Insensitive => CaseSensitivity.Insensitive,
        _ => throw new InvalidOperationException($"Unknown case policy {policy}.")
    };

    private static PortableArchiveFormat MapArchiveFormat(ArchiveFormatV1 format) => format switch
    {
        ArchiveFormatV1.SevenZip => PortableArchiveFormat.SevenZip,
        ArchiveFormatV1.Zip => PortableArchiveFormat.Zip,
        ArchiveFormatV1.TarZstd => PortableArchiveFormat.TarZstd,
        _ => throw new InvalidOperationException($"Unknown archive format {format}.")
    };

    private static PortableCompressionPreset MapCompressionPreset(CompressionPresetV1 preset) => preset switch
    {
        CompressionPresetV1.Store => PortableCompressionPreset.Store,
        CompressionPresetV1.Fast => PortableCompressionPreset.Fast,
        CompressionPresetV1.Standard => PortableCompressionPreset.Standard,
        CompressionPresetV1.Extreme => PortableCompressionPreset.Extreme,
        _ => throw new InvalidOperationException($"Unknown compression preset {preset}.")
    };

    private static PortableLinkPolicy MapLinkPolicy(LinkPolicyV1 policy) =>
        policy is LinkPolicyV1.Preserve ? PortableLinkPolicy.Preserve : PortableLinkPolicy.Skip;

    private static PortableChangeDetectionMode MapChangeDetection(ChangeDetectionModeV1 mode) =>
        mode is ChangeDetectionModeV1.Standard ? PortableChangeDetectionMode.Standard : PortableChangeDetectionMode.Strict;

    private static DayOfWeek MapDayOfWeek(DayOfWeekV1 day) => day switch
    {
        DayOfWeekV1.Monday => DayOfWeek.Monday,
        DayOfWeekV1.Tuesday => DayOfWeek.Tuesday,
        DayOfWeekV1.Wednesday => DayOfWeek.Wednesday,
        DayOfWeekV1.Thursday => DayOfWeek.Thursday,
        DayOfWeekV1.Friday => DayOfWeek.Friday,
        DayOfWeekV1.Saturday => DayOfWeek.Saturday,
        DayOfWeekV1.Sunday => DayOfWeek.Sunday,
        _ => throw new InvalidOperationException($"Unknown day of week {day}.")
    };
}

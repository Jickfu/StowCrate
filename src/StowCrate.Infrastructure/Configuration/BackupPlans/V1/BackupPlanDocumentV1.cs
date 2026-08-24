using System.Text.Json.Serialization;

namespace StowCrate.Infrastructure.Configuration.BackupPlans.V1;

public sealed record BackupPlanDocumentV1
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public required int SchemaVersion { get; init; }
    public required Guid PlanId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required PortableSemanticsPinsV1 Semantics { get; init; }
    public required IReadOnlyList<BackupSourceV1> Sources { get; init; }
    public required GlobalRulesSnapshotV1 GlobalRules { get; init; }
    public required IReadOnlyList<RuleV1> PlanRules { get; init; }
    public required ArchiveSpecV1 ArchiveSpecDefault { get; init; }
    public required IReadOnlyList<ArchiveUnitDeclarationV1> ArchiveUnits { get; init; }
    public required IReadOnlyList<SecretSlotV1> SecretSlots { get; init; }
    public required LinkPolicyV1 LinkPolicy { get; init; }
    public required ChangeDetectionV1 ChangeDetection { get; init; }
    public required HistoryPolicyV1 HistoryDefault { get; init; }
    public required ScheduleIntentV1 Schedule { get; init; }
    public required IReadOnlyList<ExternalSourceDeclarationV1> ExternalSources { get; init; }
}

public sealed record PortableSemanticsPinsV1(int Rules, int Archive, int OutputPathEncoding);
public sealed record BackupSourceV1(Guid SourceId, string Name, string SourceOutputPath);
public sealed record GlobalRulesSnapshotV1(IReadOnlyList<RuleV1> Rules, GlobalRuleProvenanceV1? Provenance = null);
public sealed record GlobalRuleProvenanceV1(string? Id = null, string? Name = null, string? Revision = null);
public sealed record RuleV1(RuleActionV1 Action, string Pattern);
public sealed record UiManagedLocalRulesV1(RuleModeV1 Mode, CasePolicyV1 Case, IReadOnlyList<RuleV1> Rules);

[JsonConverter(typeof(ArchiveUnitDeclarationV1Converter))]
public abstract record ArchiveUnitDeclarationV1
{
    public required Guid ArchiveUnitId { get; init; }
    public required Guid SourceId { get; init; }
    public required string Path { get; init; }
    public ArchiveSpecOverrideV1? ArchiveSpecOverride { get; init; }
    public HistoryOverrideV1? HistoryOverride { get; init; }
}

public sealed record UiManagedArchiveUnitV1 : ArchiveUnitDeclarationV1
{
    public required RuleSourceV1 RuleSource { get; init; }
    public required UiManagedLocalRulesV1 LocalRules { get; init; }
}

public sealed record FileManagedArchiveUnitV1 : ArchiveUnitDeclarationV1
{
    public required RuleSourceV1 RuleSource { get; init; }
}

public sealed record ArchiveSpecV1(
    ArchiveFormatV1 Format,
    CompressionPresetV1 CompressionPreset,
    ProtectionConfigurationV1 Protection);

public sealed record ArchiveSpecOverrideV1(
    ArchiveFormatV1? Format = null,
    CompressionPresetV1? CompressionPreset = null,
    ProtectionConfigurationV1? Protection = null);

[JsonConverter(typeof(ProtectionConfigurationV1Converter))]
public abstract record ProtectionConfigurationV1;
public sealed record NoProtectionV1(ProtectionModeV1 Mode) : ProtectionConfigurationV1;
public sealed record PrivacyProtectionV1(ProtectionModeV1 Mode) : ProtectionConfigurationV1;
public sealed record SecureProtectionV1(ProtectionModeV1 Mode, Guid SecretSlotId) : ProtectionConfigurationV1;

public sealed record SecretSlotV1(Guid SecretSlotId, string Name, SecretPurposeV1 Purpose);
public sealed record ChangeDetectionV1(ChangeDetectionModeV1 Mode);

[JsonConverter(typeof(HistoryPolicyV1Converter))]
public abstract record HistoryPolicyV1;
public sealed record HistoryDisabledV1(HistoryModeV1 Mode) : HistoryPolicyV1;
public sealed record HistoryEnabledV1(HistoryModeV1 Mode, RetentionPolicyV1 Retention) : HistoryPolicyV1;

[JsonConverter(typeof(HistoryOverrideV1Converter))]
public abstract record HistoryOverrideV1;
public sealed record HistoryInheritV1(HistoryOverrideModeV1 Mode) : HistoryOverrideV1;
public sealed record HistoryOverrideDisabledV1(HistoryOverrideModeV1 Mode) : HistoryOverrideV1;
public sealed record HistoryOverrideEnabledV1(HistoryOverrideModeV1 Mode, RetentionPolicyV1 Retention) : HistoryOverrideV1;

[JsonConverter(typeof(RetentionPolicyV1Converter))]
public abstract record RetentionPolicyV1;
public sealed record KeepAllRetentionV1(RetentionKindV1 Kind) : RetentionPolicyV1;
public sealed record KeepLastVersionsRetentionV1(RetentionKindV1 Kind, int Count) : RetentionPolicyV1;

[JsonConverter(typeof(ScheduleIntentV1Converter))]
public abstract record ScheduleIntentV1;
public sealed record ManualOnlyScheduleV1(bool Enabled) : ScheduleIntentV1;
public sealed record AutomaticScheduleV1(
    bool Enabled,
    IReadOnlyList<ScheduleTriggerV1> Triggers,
    MissedRunPolicyV1 MissedRunPolicy) : ScheduleIntentV1;

[JsonConverter(typeof(ScheduleTriggerV1Converter))]
public abstract record ScheduleTriggerV1;
public sealed record DailyTriggerV1(ScheduleTriggerTypeV1 Type, string LocalTime) : ScheduleTriggerV1;
public sealed record WeeklyTriggerV1(ScheduleTriggerTypeV1 Type, IReadOnlyList<DayOfWeekV1> DaysOfWeek, string LocalTime) : ScheduleTriggerV1;
public sealed record OnStartupTriggerV1(ScheduleTriggerTypeV1 Type) : ScheduleTriggerV1;

public sealed record ExternalSourceDeclarationV1(
    Guid ExternalSourceId,
    string Name,
    ExternalSourceKindV1 Kind,
    Guid TargetArchiveUnitId,
    string ArchiveDestination);

[JsonConverter(typeof(JsonStringEnumConverter<RuleActionV1>))]
public enum RuleActionV1 { Include, Exclude }
[JsonConverter(typeof(JsonStringEnumConverter<RuleModeV1>))]
public enum RuleModeV1 { Exclude, IncludeOnly }
[JsonConverter(typeof(JsonStringEnumConverter<RuleSourceV1>))]
public enum RuleSourceV1 { UiManaged, FileManaged }
[JsonConverter(typeof(JsonStringEnumConverter<CasePolicyV1>))]
public enum CasePolicyV1 { Auto, Sensitive, Insensitive }
[JsonConverter(typeof(JsonStringEnumConverter<ArchiveFormatV1>))]
public enum ArchiveFormatV1 { SevenZip, Zip, TarZstd }
[JsonConverter(typeof(JsonStringEnumConverter<CompressionPresetV1>))]
public enum CompressionPresetV1 { Store, Fast, Standard, Extreme }
[JsonConverter(typeof(JsonStringEnumConverter<ProtectionModeV1>))]
public enum ProtectionModeV1 { None, Privacy, Secure }
[JsonConverter(typeof(JsonStringEnumConverter<SecretPurposeV1>))]
public enum SecretPurposeV1 { ArchiveEncryption }
[JsonConverter(typeof(JsonStringEnumConverter<LinkPolicyV1>))]
public enum LinkPolicyV1 { Preserve, Skip }
[JsonConverter(typeof(JsonStringEnumConverter<ChangeDetectionModeV1>))]
public enum ChangeDetectionModeV1 { Standard, Strict }
[JsonConverter(typeof(JsonStringEnumConverter<HistoryModeV1>))]
public enum HistoryModeV1 { Disabled, Enabled }
[JsonConverter(typeof(JsonStringEnumConverter<HistoryOverrideModeV1>))]
public enum HistoryOverrideModeV1 { Inherit, Disabled, Enabled }
[JsonConverter(typeof(JsonStringEnumConverter<RetentionKindV1>))]
public enum RetentionKindV1 { KeepAll, KeepLastVersions }
[JsonConverter(typeof(JsonStringEnumConverter<ScheduleTriggerTypeV1>))]
public enum ScheduleTriggerTypeV1 { Daily, Weekly, OnStartup }
[JsonConverter(typeof(JsonStringEnumConverter<DayOfWeekV1>))]
public enum DayOfWeekV1 { Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday }
[JsonConverter(typeof(JsonStringEnumConverter<MissedRunPolicyV1>))]
public enum MissedRunPolicyV1 { Skip, RunOnceWhenAvailable }
[JsonConverter(typeof(JsonStringEnumConverter<ExternalSourceKindV1>))]
public enum ExternalSourceKindV1 { File, Directory }

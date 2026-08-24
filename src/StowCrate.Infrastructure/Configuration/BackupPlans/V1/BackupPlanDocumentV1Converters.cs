using System.Text.Json;
using System.Text.Json.Serialization;

namespace StowCrate.Infrastructure.Configuration.BackupPlans.V1;

internal abstract class DiscriminatedUnionConverter<T> : JsonConverter<T>
{
    public sealed override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var targetType = SelectType(document.RootElement);
        return (T?)JsonSerializer.Deserialize(document.RootElement.GetRawText(), targetType, options);
    }

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value!.GetType(), options);

    protected abstract Type SelectType(JsonElement element);

    protected static string StringDiscriminator(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new JsonException($"Property '{propertyName}' must be a string.");
}

internal sealed class ArchiveUnitDeclarationV1Converter : DiscriminatedUnionConverter<ArchiveUnitDeclarationV1>
{
    protected override Type SelectType(JsonElement element) => StringDiscriminator(element, "ruleSource") switch
    {
        "uiManaged" => typeof(UiManagedArchiveUnitV1),
        "fileManaged" => typeof(FileManagedArchiveUnitV1),
        _ => throw new JsonException("Unknown archive-unit ruleSource.")
    };
}

internal sealed class ProtectionConfigurationV1Converter : DiscriminatedUnionConverter<ProtectionConfigurationV1>
{
    protected override Type SelectType(JsonElement element) => StringDiscriminator(element, "mode") switch
    {
        "none" => typeof(NoProtectionV1),
        "privacy" => typeof(PrivacyProtectionV1),
        "secure" => typeof(SecureProtectionV1),
        _ => throw new JsonException("Unknown protection mode.")
    };
}

internal sealed class HistoryPolicyV1Converter : DiscriminatedUnionConverter<HistoryPolicyV1>
{
    protected override Type SelectType(JsonElement element) => StringDiscriminator(element, "mode") switch
    {
        "disabled" => typeof(HistoryDisabledV1),
        "enabled" => typeof(HistoryEnabledV1),
        _ => throw new JsonException("Unknown history mode.")
    };
}

internal sealed class HistoryOverrideV1Converter : DiscriminatedUnionConverter<HistoryOverrideV1>
{
    protected override Type SelectType(JsonElement element) => StringDiscriminator(element, "mode") switch
    {
        "inherit" => typeof(HistoryInheritV1),
        "disabled" => typeof(HistoryOverrideDisabledV1),
        "enabled" => typeof(HistoryOverrideEnabledV1),
        _ => throw new JsonException("Unknown history override mode.")
    };
}

internal sealed class RetentionPolicyV1Converter : DiscriminatedUnionConverter<RetentionPolicyV1>
{
    protected override Type SelectType(JsonElement element) => StringDiscriminator(element, "kind") switch
    {
        "keepAll" => typeof(KeepAllRetentionV1),
        "keepLastVersions" => typeof(KeepLastVersionsRetentionV1),
        _ => throw new JsonException("Unknown retention kind.")
    };
}

internal sealed class ScheduleIntentV1Converter : DiscriminatedUnionConverter<ScheduleIntentV1>
{
    protected override Type SelectType(JsonElement element) => element.GetProperty("enabled").GetBoolean()
        ? typeof(AutomaticScheduleV1)
        : typeof(ManualOnlyScheduleV1);
}

internal sealed class ScheduleTriggerV1Converter : DiscriminatedUnionConverter<ScheduleTriggerV1>
{
    protected override Type SelectType(JsonElement element) => StringDiscriminator(element, "type") switch
    {
        "daily" => typeof(DailyTriggerV1),
        "weekly" => typeof(WeeklyTriggerV1),
        "onStartup" => typeof(OnStartupTriggerV1),
        _ => throw new JsonException("Unknown schedule trigger type.")
    };
}

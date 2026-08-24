using System.Text;
using System.Text.Json.Nodes;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;

namespace StowCrate.Infrastructure.Tests.Configuration;

public sealed class BackupPlanDocumentV1WriterTests
{
    private static readonly string ValidFixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "schemas",
        "fixtures",
        "backupplan-v1",
        "valid");

    private readonly BackupPlanDocumentV1Reader reader = new();
    private readonly BackupPlanDocumentV1Writer writer = new();

    [Fact]
    public void GeneratedBytesPassStrictReaderAndPreservePortableSemantics()
    {
        var original = LoadPlan("secure-schedule-external.json");

        var write = writer.Write(original);
        var read = reader.Read(write.Bytes!);
        var mapped = BackupPlanDocumentV1Mapper.Map(read.Document!);

        Assert.True(write.IsSuccess, write.Error?.ToString());
        Assert.True(read.IsSuccess, read.Error?.ToString());
        Assert.True(mapped.IsSuccess, string.Join(Environment.NewLine, mapped.Errors));
        Assert.Equal(original.Id, mapped.Plan!.Id);
        Assert.Equal(original.Name, mapped.Plan.Name);
        Assert.Equal(original.Sources.Select(x => x.Id), mapped.Plan.Sources.Select(x => x.Id));
        Assert.IsType<SecureProtection>(mapped.Plan.ArchiveSpecDefault.Protection);
        Assert.IsType<AutomaticSchedule>(mapped.Plan.Schedule);
        Assert.Single(mapped.Plan.ExternalSources);
    }

    [Fact]
    public void ReadWriteReadIsCanonicallyIdempotent()
    {
        var first = writer.Write(LoadPlan("secure-schedule-external.json"));
        var mapped = BackupPlanDocumentV1Mapper.Map(reader.Read(first.Bytes!).Document!);
        var second = writer.Write(mapped.Plan!);

        Assert.Equal(first.Bytes, second.Bytes);
    }

    [Fact]
    public void UnorderedAggregateAndSchedulePermutationsProduceIdenticalBytes()
    {
        var original = CreateMultiItemPlan();
        var schedule = Assert.IsType<AutomaticSchedule>(original.Schedule);
        var permutedSchedule = new AutomaticSchedule(
            schedule.Triggers.Reverse().Select(trigger => trigger is WeeklyScheduleTrigger weekly
                ? new WeeklyScheduleTrigger(weekly.DaysOfWeek.Reverse(), weekly.LocalTime)
                : trigger),
            schedule.MissedRunPolicy);
        var permuted = Copy(
            original,
            sources: original.Sources.Reverse(),
            archiveUnits: original.ArchiveUnits.Reverse(),
            secretSlots: original.SecretSlots.Reverse(),
            schedule: permutedSchedule,
            externalSources: original.ExternalSources.Reverse());

        Assert.Equal(writer.Write(original).Bytes, writer.Write(permuted).Bytes);
    }

    [Fact]
    public void RuleArraysPreserveAuthoredOrderAndUseNormalizedPatterns()
    {
        var original = LoadPlan("minimal-ui-managed.json");
        var globalRules = new GlobalRulesSnapshot(
        [
            new BackupRule(RuleAction.Exclude, "  z/**  "),
            new BackupRule(RuleAction.Include, "a/**")
        ],
        null);
        var planRules = new[]
        {
            new BackupRule(RuleAction.Include, "plan-first"),
            new BackupRule(RuleAction.Exclude, "plan-second")
        };
        var originalUnit = Assert.IsType<UiManagedArchiveUnit>(original.ArchiveUnits[0]);
        var unit = new UiManagedArchiveUnit(
            originalUnit.Id,
            originalUnit.SourceId,
            originalUnit.Path,
            new RuleSet(
                originalUnit.LocalRules.Mode,
                originalUnit.LocalRules.CaseSensitivity,
                [
                    new BackupRule(RuleAction.Exclude, "local-first"),
                    new BackupRule(RuleAction.Include, "local-second")
                ]),
            originalUnit.ArchiveSpecOverride,
            originalUnit.HistoryOverride);
        var plan = Copy(original, globalRules: globalRules, planRules: planRules, archiveUnits: [unit]);

        var root = Parse(writer.Write(plan).Bytes!);
        var global = root["globalRules"]!["rules"]!.AsArray();
        var rules = root["planRules"]!.AsArray();
        var local = root["archiveUnits"]![0]!["localRules"]!["rules"]!.AsArray();

        Assert.Equal(["z/**", "a/**"], global.Select(rule => rule!["pattern"]!.GetValue<string>()));
        Assert.Equal(["plan-first", "plan-second"], rules.Select(rule => rule!["pattern"]!.GetValue<string>()));
        Assert.Equal(["local-first", "local-second"], local.Select(rule => rule!["pattern"]!.GetValue<string>()));
        Assert.Equal("exclude", global[0]!["action"]!.GetValue<string>());
        Assert.Equal("include", global[1]!["action"]!.GetValue<string>());
    }

    [Fact]
    public void AuthoredHistoryInheritAndExplicitDisabledRemainDistinct()
    {
        var original = LoadPlan("secure-schedule-external.json");
        var inherited = original.ArchiveUnits[0];
        var explicitDisabled = new FileManagedArchiveUnit(
            new ArchiveUnitId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
            inherited.SourceId,
            new LogicalPath("other"),
            new AuthoredArchiveSpecOverride(PortableArchiveFormat.Zip, null, new PrivacyProtection()),
            new HistoryOverrideDisabled());
        var plan = Copy(original, archiveUnits: [inherited, explicitDisabled]);

        var units = Parse(writer.Write(plan).Bytes!)["archiveUnits"]!.AsArray();
        var modes = units.ToDictionary(
            unit => unit!["path"]!.GetValue<string>(),
            unit => unit!["historyOverride"]!["mode"]!.GetValue<string>(),
            StringComparer.Ordinal);

        Assert.Equal("inherit", modes["work"]);
        Assert.Equal("disabled", modes["other"]);
        Assert.Equal("zip", units.Single(unit => unit!["path"]!.GetValue<string>() == "other")!["archiveSpecOverride"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void FormattingAndFrozenEnumStringsAreCanonical()
    {
        var bytes = writer.Write(LoadPlan("secure-schedule-external.json")).Bytes!;
        var text = Encoding.UTF8.GetString(bytes);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"schemaVersion\": 1,", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$schema\"", text, StringComparison.Ordinal);
        Assert.Contains("\"format\": \"tarZstd\"", text, StringComparison.Ordinal);
        Assert.Contains("\"missedRunPolicy\": \"runOnceWhenAvailable\"", text, StringComparison.Ordinal);
        Assert.Contains("\"purpose\": \"archiveEncryption\"", text, StringComparison.Ordinal);
        Assert.Contains("\"localTime\": \"02:00\"", text, StringComparison.Ordinal);
        Assert.Contains("\"planId\": \"ae8d85e7-2f28-4f79-a2a8-83f319a41ea5\"", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("\"schemaVersion\"", StringComparison.Ordinal) < text.IndexOf("\"planId\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"planId\"", StringComparison.Ordinal) < text.IndexOf("\"name\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"name\"", StringComparison.Ordinal) < text.IndexOf("\"semantics\"", StringComparison.Ordinal));

        var sevenZipText = Encoding.UTF8.GetString(writer.Write(LoadPlan("minimal-ui-managed.json")).Bytes!);
        Assert.Contains("\"format\": \"sevenZip\"", sevenZipText, StringComparison.Ordinal);
    }

    [Fact]
    public void WeekdaysUseExplicitMondayThroughSundayOrder()
    {
        var original = LoadPlan("secure-schedule-external.json");
        var schedule = Assert.IsType<AutomaticSchedule>(original.Schedule);
        var weekly = Assert.Single(schedule.Triggers.OfType<WeeklyScheduleTrigger>());
        var replacement = new WeeklyScheduleTrigger(
            [DayOfWeek.Sunday, DayOfWeek.Wednesday, DayOfWeek.Monday],
            weekly.LocalTime);
        var plan = Copy(
            original,
            schedule: new AutomaticSchedule(
                schedule.Triggers.Where(trigger => trigger is not WeeklyScheduleTrigger).Append(replacement),
                schedule.MissedRunPolicy));

        var triggers = Parse(writer.Write(plan).Bytes!)["schedule"]!["triggers"]!.AsArray();
        var days = triggers.Single(trigger => trigger!["type"]!.GetValue<string>() == "weekly")!["daysOfWeek"]!.AsArray();

        Assert.Equal(["monday", "wednesday", "sunday"], days.Select(day => day!.GetValue<string>()));
    }

    [Fact]
    public void UnsupportedSemanticsAreRejectedWithoutRewritingPins()
    {
        var plan = Copy(
            LoadPlan("minimal-ui-managed.json"),
            semantics: new PortableSemanticsPins(1, 2, 1));

        var result = writer.Write(plan);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Bytes);
        Assert.Equal(BackupPlanDocumentWriteErrorCode.SemanticValidationFailed, result.Error?.Code);
        Assert.Contains(result.Error!.SemanticErrors!, error =>
            error.Code == BackupPlanSemanticErrorCode.UnsupportedDocumentSemantics
            && error.Location == "/semantics/archive");
    }

    [Fact]
    public async Task WritesValidatedBytesToSyncAndAsyncStreams()
    {
        var plan = LoadPlan("minimal-ui-managed.json");
        using var sync = new MemoryStream();
        await using var asyncStream = new MemoryStream();

        var syncError = writer.Write(sync, plan);
        var asyncError = await writer.WriteAsync(asyncStream, plan, CancellationToken.None);

        Assert.Null(syncError);
        Assert.Null(asyncError);
        Assert.Equal(sync.ToArray(), asyncStream.ToArray());
        Assert.True(reader.Read(sync.ToArray()).IsSuccess);
    }

    private PortableBackupPlan LoadPlan(string fixtureName)
    {
        var document = reader.Read(File.ReadAllBytes(Path.Combine(ValidFixtureRoot, fixtureName))).Document!;
        if (fixtureName == "file-managed-overrides.json")
        {
            document = document with { Semantics = new PortableSemanticsPinsV1(1, 1, 1) };
        }

        return BackupPlanDocumentV1Mapper.Map(document).Plan!;
    }

    private PortableBackupPlan CreateMultiItemPlan()
    {
        var original = LoadPlan("secure-schedule-external.json");
        var secondSource = new PortableBackupSource(
            new SourceId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            "Second",
            new LogicalPath("second"));
        var secondUnit = new FileManagedArchiveUnit(
            new ArchiveUnitId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            secondSource.Id,
            new LogicalPath("unit"),
            null,
            null);
        var secondSlot = new PortableSecretSlot(
            new SecretSlotId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
            "Second secret");
        var secondExternal = new PortableExternalSource(
            new ExternalSourceId(Guid.Parse("44444444-4444-4444-8444-444444444444")),
            "Second external",
            PortableExternalSourceKind.Directory,
            secondUnit.Id,
            new LogicalPath("payload"));

        return Copy(
            original,
            sources: original.Sources.Append(secondSource),
            archiveUnits: original.ArchiveUnits.Append(secondUnit),
            secretSlots: original.SecretSlots.Append(secondSlot),
            externalSources: original.ExternalSources.Append(secondExternal));
    }

    private static PortableBackupPlan Copy(
        PortableBackupPlan plan,
        PortableSemanticsPins? semantics = null,
        IEnumerable<PortableBackupSource>? sources = null,
        GlobalRulesSnapshot? globalRules = null,
        IEnumerable<BackupRule>? planRules = null,
        IEnumerable<AuthoredArchiveUnit>? archiveUnits = null,
        IEnumerable<PortableSecretSlot>? secretSlots = null,
        PortableScheduleIntent? schedule = null,
        IEnumerable<PortableExternalSource>? externalSources = null) =>
        new(
            plan.Id,
            plan.Name,
            plan.Description,
            semantics ?? plan.Semantics,
            sources ?? plan.Sources,
            globalRules ?? plan.GlobalRules,
            planRules ?? plan.PlanRules,
            plan.ArchiveSpecDefault,
            archiveUnits ?? plan.ArchiveUnits,
            secretSlots ?? plan.SecretSlots,
            plan.LinkPolicy,
            plan.ChangeDetection,
            plan.HistoryDefault,
            schedule ?? plan.Schedule,
            externalSources ?? plan.ExternalSources);

    private static JsonObject Parse(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();
}

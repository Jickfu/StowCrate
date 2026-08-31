using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Rules;

namespace StowCrate.Application.BackupPlans.Candidates;

public sealed record StorageBindingFingerprintFacts(
    int SemanticsVersion,
    string DestinationFileSystemCapabilityIdentity);

public enum CandidateFingerprintErrorCode { MissingStrictContentHash, MissingRuleSourceRawHash }
public sealed record CandidateFingerprintError(CandidateFingerprintErrorCode Code, string Message, Core.Paths.RelativePath? Path = null);
public sealed record CandidateFingerprintResult(CandidateArchiveFingerprints? Fingerprints, ImmutableArray<CandidateFingerprintError> Errors);

public static class CandidateFingerprintCalculator
{
    public static PlanSemanticFingerprint ComputePlanSemantic(PortableBackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PlanSemanticFingerprint(CanonicalFingerprintEncodingV1.Encode("plan-semantic", writer =>
        {
            writer.Utf8(1, plan.Id.Value.ToString("D"));
            writer.Utf8(2, plan.Name);
            writer.Utf8(3, plan.Description ?? string.Empty);
            writer.SignedNumber(4, plan.Semantics.Rules);
            writer.SignedNumber(5, plan.Semantics.Archive);
            writer.SignedNumber(6, plan.Semantics.OutputPathEncoding);
            foreach (var source in plan.Sources.OrderBy(item => item.Id.Value.ToString("D"), StringComparer.Ordinal))
                writer.Digest(10, CanonicalFingerprintEncodingV1.Encode("authored-source", nested => { nested.Utf8(1, source.Id.Value.ToString("D")); nested.Utf8(2, source.Name); nested.Utf8(3, source.SourceOutputPath.Value); }));
            WriteRules(writer, 20, plan.GlobalRules.Rules);
            WriteRules(writer, 21, plan.PlanRules);
            writer.Digest(30, AuthoredArchiveSpecDigest(plan.ArchiveSpecDefault));
            foreach (var unit in plan.ArchiveUnits.OrderBy(item => item.Id.Value.ToString("D"), StringComparer.Ordinal))
                writer.Digest(31, AuthoredUnitDigest(unit));
            foreach (var slot in plan.SecretSlots.OrderBy(item => item.Id.Value.ToString("D"), StringComparer.Ordinal))
                writer.Digest(32, CanonicalFingerprintEncodingV1.Encode("secret-slot", nested => { nested.Utf8(1, slot.Id.Value.ToString("D")); nested.Utf8(2, slot.Name); }));
            writer.SignedNumber(40, (int)plan.LinkPolicy);
            writer.SignedNumber(41, (int)plan.ChangeDetection);
            writer.Digest(42, AuthoredHistoryDigest(plan.HistoryDefault));
            writer.Digest(43, ScheduleDigest(plan.Schedule));
            foreach (var external in plan.ExternalSources.OrderBy(item => item.Id.Value.ToString("D"), StringComparer.Ordinal))
                writer.Digest(44, CanonicalFingerprintEncodingV1.Encode("authored-external", nested =>
                {
                    nested.Utf8(1, external.Id.Value.ToString("D")); nested.Utf8(2, external.Name);
                    nested.SignedNumber(3, (int)external.Kind); nested.Utf8(4, external.TargetArchiveUnitId.Value.ToString("D")); nested.Utf8(5, external.ArchiveDestination.Value);
                }));
        }));
    }

    public static CandidateFingerprintResult Compute(
        ResolvedPlanSnapshot plan,
        ExecutionReadyArchive ready,
        StorageBindingFingerprintFacts storageFacts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(storageFacts);
        var errors = ready.Candidate.Entries
            .Where(entry => entry.OwnerKind is not CandidateEntryOwnerKind.Generated
                && entry.Kind is FileSystemEntryKind.File
                && plan.ChangeDetection is PortableChangeDetectionMode.Strict
                && entry.ContentIdentity.Kind is not ObservedContentIdentityKind.FullContentSha256V1)
            .Select(entry => new CandidateFingerprintError(CandidateFingerprintErrorCode.MissingStrictContentHash, "Strict v1 requires a full SHA-256 content identity for every regular candidate file.", entry.ArchivePath))
            .Concat(ready.Candidate.Entries
                .Where(entry => entry.OwnerKind is CandidateEntryOwnerKind.Normal
                    && entry.ArchivePath.Name == ".backupignore"
                    && entry.RawFileSha256 is null)
                .Select(entry => new CandidateFingerprintError(CandidateFingerprintErrorCode.MissingRuleSourceRawHash, ".backupignore requires its actual raw-byte SHA-256 identity.", entry.ArchivePath)))
            .ToImmutableArray();
        if (!errors.IsEmpty) return new CandidateFingerprintResult(null, errors);

        var archive = ready.Candidate;
        var unit = archive.Unit;
        var externalMappings = plan.ExternalSources.Where(item => item.TargetArchiveUnitId == unit.ArchiveUnitId).ToArray();
        var entrySet = new EntrySetFingerprint(CanonicalFingerprintEncodingV1.Encode("entry-set", writer =>
        {
            writer.SignedNumber(1, (int)plan.ChangeDetection);
            foreach (var entry in archive.Entries.Where(item => item.OwnerKind is not CandidateEntryOwnerKind.Generated).OrderBy(item => item.ArchivePath.Value, StringComparer.Ordinal))
                writer.Digest(2, EntryDigest(entry));
        }));
        var rules = Diagnostic("rules", writer =>
        {
            writer.SignedNumber(1, plan.Semantics.Rules);
            WriteRules(writer, 10, plan.GlobalRules);
            WriteRules(writer, 20, plan.PlanRules);
            writer.SignedNumber(30, (int)unit.LocalRuleSet.Mode);
            writer.SignedNumber(31, (int)unit.LocalRuleSet.CaseSensitivity);
            writer.SignedNumber(32, (int)unit.EffectiveRuleSet.ResolvedCaseSensitivity);
            WriteRules(writer, 40, unit.LocalRuleSet.Rules);
        });
        var linkPolicy = Diagnostic("link-policy", writer => writer.SignedNumber(1, (int)plan.LinkPolicy));
        var boundary = Diagnostic("boundary", writer =>
        {
            writer.Utf8(1, unit.Root.Value);
            writer.SortedUtf8(2, archive.ChildBoundaryRoots.Select(path => path.Value));
        });
        var external = Diagnostic("external-mapping", writer =>
        {
            foreach (var mapping in externalMappings.OrderBy(item => item.ArchiveDestination.Value, StringComparer.Ordinal).ThenBy(item => (int)item.Kind))
            {
                writer.Digest(1, CanonicalFingerprintEncodingV1.Encode("external-map", nested =>
                {
                    nested.SignedNumber(1, (int)mapping.Kind);
                    nested.Utf8(2, mapping.ArchiveDestination.Value);
                }));
            }
        });
        var selection = new SelectionFingerprint(CanonicalFingerprintEncodingV1.Encode("selection", writer =>
        {
            writer.SignedNumber(1, plan.Semantics.Rules);
            writer.Utf8(2, unit.SourceId.Value.ToString("D"));
            writer.Utf8(3, unit.Root.Value);
            writer.Digest(4, rules.Digest);
            writer.Digest(5, boundary.Digest);
            writer.Digest(6, external.Digest);
            writer.Digest(7, linkPolicy.Digest);
        }));

        var format = Diagnostic("format", writer => writer.SignedNumber(1, (int)unit.ArchiveSpec.Format));
        var compression = Diagnostic("compression", writer => writer.SignedNumber(1, (int)unit.ArchiveSpec.CompressionPreset));
        var protection = Diagnostic("protection", writer => WriteProtection(writer, unit.ArchiveSpec.Protection, ready.SecureRequirement));
        var manifest = Diagnostic("manifest", writer => writer.SignedNumber(1, archive.GeneratedMetadata.ManifestSchemaVersion));
        var archiveSpec = new ArchiveSpecFingerprint(CanonicalFingerprintEncodingV1.Encode("archive-spec", writer =>
        {
            writer.SignedNumber(1, plan.Semantics.Archive);
            writer.Digest(2, format.Digest);
            writer.Digest(3, compression.Digest);
            writer.Digest(4, protection.Digest);
            writer.Utf8(5, ready.Capability.CapabilitySemantics);
            writer.Digest(6, manifest.Digest);
        }));
        var source = plan.Sources.Single(item => item.SourceId == unit.SourceId);
        var output = new OutputLayoutFingerprint(CanonicalFingerprintEncodingV1.Encode("output-layout", writer =>
        {
            writer.SignedNumber(1, plan.Semantics.OutputPathEncoding);
            writer.Utf8(2, source.SourceOutputPath.Value);
            writer.Utf8(3, unit.Root.Value);
            writer.Utf8(4, archive.OutputRelativePath.Value);
        }));
        var executionSemantic = new ExecutionSemanticFingerprint(CanonicalFingerprintEncodingV1.Encode("execution-semantic", writer =>
        {
            writer.Digest(1, selection.Digest);
            // 这里只编码 portable/effective 语义；SecretRevision 与 resolved capability 保持为独立 local stale facts。
            writer.SignedNumber(2, plan.Semantics.Archive);
            writer.Digest(3, format.Digest);
            writer.Digest(4, compression.Digest);
            writer.Digest(5, EffectiveProtectionDigest(unit.ArchiveSpec.Protection));
            writer.Digest(6, output.Digest);
            writer.Boolean(7, unit.History is EffectiveHistoryEnabled);
            writer.SignedNumber(8, archive.GeneratedMetadata.ManifestSchemaVersion);
        }));
        var binding = new ExecutionBindingFingerprint(CanonicalFingerprintEncodingV1.Encode("execution-binding", writer =>
        {
            writer.SignedNumber(1, storageFacts.SemanticsVersion);
            writer.Utf8(2, source.PhysicalRoot.CanonicalPath);
            writer.Utf8(3, plan.CurrentRoot.CanonicalPath);
            if (unit.History is EffectiveHistoryEnabled) writer.Utf8(4, plan.HistoryRoot!.CanonicalPath);
            writer.SortedUtf8(5, plan.ExternalSources.Where(item => item.TargetArchiveUnitId == unit.ArchiveUnitId).Select(item => item.PhysicalInput.CanonicalPath));
            writer.Utf8(6, storageFacts.DestinationFileSystemCapabilityIdentity);
        }));
        return new CandidateFingerprintResult(new CandidateArchiveFingerprints(
            CanonicalFingerprintEncodingV1.Version,
            plan.Semantics,
            true,
            entrySet, selection, archiveSpec, output, executionSemantic, binding,
            new CandidateComponentFingerprints(rules, boundary, linkPolicy, external, format, compression, protection, manifest)), []);
    }

    private static Sha256Digest EntryDigest(CandidateArchiveEntry entry) => CanonicalFingerprintEncodingV1.Encode("entry", writer =>
    {
        writer.Utf8(1, entry.ArchivePath.Value);
        writer.SignedNumber(2, (int)entry.Kind);
        writer.SignedNumber(3, entry.Length);
        writer.Utf8(4, entry.LastWriteTimeUtc?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        writer.SignedNumber(5, (int)entry.MetadataFlags);
        writer.SignedNumber(6, (int)entry.ContentIdentity.Kind);
        if (entry.ContentIdentity.FullContentDigest is { } digest) writer.Digest(7, digest);
        if (entry.ArchivePath.Name == ".backupignore" && entry.RawFileSha256 is { } rawDigest) writer.Digest(8, rawDigest);
        if (entry.Link is { } link)
        {
            writer.SignedNumber(10, (int)link.Kind);
            writer.Utf8(11, link.Target);
            writer.SignedNumber(12, (int)link.TargetScope);
            writer.Boolean(13, link.IsDangling);
        }
    });

    private static DiagnosticFingerprint Diagnostic(string kind, Action<CanonicalFingerprintWriter> write) => new(CanonicalFingerprintEncodingV1.Encode(kind, write));

    private static void WriteRules(CanonicalFingerprintWriter writer, int field, IEnumerable<BackupRule> rules)
    {
        foreach (var rule in rules)
            writer.Digest(field, CanonicalFingerprintEncodingV1.Encode("rule", nested => { nested.SignedNumber(1, (int)rule.Action); nested.Utf8(2, rule.Pattern); }));
    }

    private static void WriteProtection(CanonicalFingerprintWriter writer, AuthoredProtection protection, SecureRevisionRequirement? secure)
    {
        switch (protection)
        {
            case NoProtection: writer.SignedNumber(1, 0); break;
            case PrivacyProtection:
                writer.SignedNumber(1, 1);
                writer.SignedNumber(2, CandidateRuntimeSemantics.PrivacyProtectionVersion);
                break;
            case SecureProtection value:
                writer.SignedNumber(1, 2);
                writer.Utf8(3, value.SecretSlotId.Value.ToString("D"));
                writer.SignedNumber(4, secure!.SecretRevision.Value);
                break;
            default: throw new InvalidOperationException("Unknown protection variant.");
        }
    }

    private static Sha256Digest EffectiveProtectionDigest(AuthoredProtection protection) => CanonicalFingerprintEncodingV1.Encode("effective-protection", writer =>
    {
        switch (protection)
        {
            case NoProtection: writer.SignedNumber(1, 0); break;
            case PrivacyProtection: writer.SignedNumber(1, 1); writer.SignedNumber(2, CandidateRuntimeSemantics.PrivacyProtectionVersion); break;
            case SecureProtection secure: writer.SignedNumber(1, 2); writer.Utf8(3, secure.SecretSlotId.Value.ToString("D")); break;
            default: throw new InvalidOperationException("Unknown protection variant.");
        }
    });

    private static Sha256Digest AuthoredArchiveSpecDigest(AuthoredArchiveSpec spec) => CanonicalFingerprintEncodingV1.Encode("authored-archive-spec", writer =>
    {
        writer.SignedNumber(1, (int)spec.Format); writer.SignedNumber(2, (int)spec.CompressionPreset); writer.Digest(3, EffectiveProtectionDigest(spec.Protection));
    });

    private static Sha256Digest AuthoredUnitDigest(AuthoredArchiveUnit unit) => CanonicalFingerprintEncodingV1.Encode("authored-unit", writer =>
    {
        writer.Utf8(1, unit.Id.Value.ToString("D")); writer.Utf8(2, unit.SourceId.Value.ToString("D")); writer.Utf8(3, unit.Path.Value);
        writer.SignedNumber(4, unit is UiManagedArchiveUnit ? 1 : 2);
        if (unit is UiManagedArchiveUnit ui) { writer.SignedNumber(5, (int)ui.LocalRules.Mode); writer.SignedNumber(6, (int)ui.LocalRules.CaseSensitivity); WriteRules(writer, 7, ui.LocalRules.Rules); }
        if (unit.ArchiveSpecOverride is null) writer.SignedNumber(10, 0);
        else
        {
            writer.SignedNumber(10, 1);
            writer.SignedNumber(11, unit.ArchiveSpecOverride.Format is null ? -1 : (int)unit.ArchiveSpecOverride.Format.Value);
            writer.SignedNumber(12, unit.ArchiveSpecOverride.CompressionPreset is null ? -1 : (int)unit.ArchiveSpecOverride.CompressionPreset.Value);
            if (unit.ArchiveSpecOverride.Protection is null) writer.SignedNumber(13, -1); else writer.Digest(14, EffectiveProtectionDigest(unit.ArchiveSpecOverride.Protection));
        }
        if (unit.HistoryOverride is null) writer.SignedNumber(20, 0); else writer.Digest(21, AuthoredHistoryOverrideDigest(unit.HistoryOverride));
    });

    private static Sha256Digest AuthoredHistoryDigest(AuthoredHistoryPolicy history) => CanonicalFingerprintEncodingV1.Encode("authored-history", writer =>
    {
        if (history is HistoryDisabled) writer.SignedNumber(1, 0);
        else if (history is HistoryEnabled enabled) { writer.SignedNumber(1, 1); WriteRetention(writer, enabled.Retention); }
    });

    private static Sha256Digest AuthoredHistoryOverrideDigest(AuthoredHistoryOverride history) => CanonicalFingerprintEncodingV1.Encode("authored-history-override", writer =>
    {
        switch (history)
        {
            case HistoryInherit: writer.SignedNumber(1, 0); break;
            case HistoryOverrideDisabled: writer.SignedNumber(1, 1); break;
            case HistoryOverrideEnabled enabled: writer.SignedNumber(1, 2); WriteRetention(writer, enabled.Retention); break;
        }
    });

    private static void WriteRetention(CanonicalFingerprintWriter writer, AuthoredRetentionPolicy retention)
    {
        if (retention is KeepAllRetention) writer.SignedNumber(2, 0);
        else if (retention is KeepLastVersionsRetention keep) { writer.SignedNumber(2, 1); writer.SignedNumber(3, keep.Count); }
    }

    private static Sha256Digest ScheduleDigest(PortableScheduleIntent schedule) => CanonicalFingerprintEncodingV1.Encode("schedule", writer =>
    {
        if (schedule is ManualOnlySchedule) { writer.SignedNumber(1, 0); return; }
        var automatic = (AutomaticSchedule)schedule; writer.SignedNumber(1, 1); writer.SignedNumber(2, (int)automatic.MissedRunPolicy);
        foreach (var trigger in automatic.Triggers.Select(TriggerDigest).OrderBy(value => value.Value, StringComparer.Ordinal)) writer.Digest(3, trigger);
    });

    private static Sha256Digest TriggerDigest(PortableScheduleTrigger trigger) => CanonicalFingerprintEncodingV1.Encode("schedule-trigger", writer =>
    {
        switch (trigger)
        {
            case DailyScheduleTrigger daily: writer.SignedNumber(1, 0); writer.Utf8(2, daily.LocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)); break;
            case WeeklyScheduleTrigger weekly:
                writer.SignedNumber(1, 1); writer.Utf8(2, weekly.LocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
                foreach (var day in weekly.DaysOfWeek.OrderBy(day => ((int)day + 6) % 7)) writer.SignedNumber(3, (int)day);
                break;
            case OnStartupScheduleTrigger: writer.SignedNumber(1, 2); break;
        }
    });
}

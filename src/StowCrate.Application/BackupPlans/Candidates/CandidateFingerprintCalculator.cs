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
            writer.Digest(2, archiveSpec.Digest);
            writer.Digest(3, output.Digest);
            writer.Boolean(4, unit.History is EffectiveHistoryEnabled);
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
}

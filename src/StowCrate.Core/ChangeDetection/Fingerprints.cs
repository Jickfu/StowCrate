using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Core.ChangeDetection;

public readonly record struct Sha256Digest
{
    public Sha256Digest(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
            throw new ArgumentException("SHA-256 digest must be 64 lowercase hexadecimal characters.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public static Sha256Digest Hash(ReadOnlySpan<byte> bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)));
}

public readonly record struct EntrySetFingerprint(Sha256Digest Digest);
public readonly record struct SelectionFingerprint(Sha256Digest Digest);
public readonly record struct ArchiveSpecFingerprint(Sha256Digest Digest);
public readonly record struct OutputLayoutFingerprint(Sha256Digest Digest);
public readonly record struct ExecutionSemanticFingerprint(Sha256Digest Digest);
public readonly record struct ExecutionBindingFingerprint(Sha256Digest Digest);
public readonly record struct DiagnosticFingerprint(Sha256Digest Digest);

public static class CanonicalFingerprintEncodingV1
{
    public const int Version = 1;

    public static Sha256Digest Encode(string kind, Action<CanonicalFingerprintWriter> write)
    {
        // durable 编码以 kind 和 length-delimited fields 定界，禁止依赖 serializer、culture 或运行时 hash。
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(write);
        var writer = new CanonicalFingerprintWriter(kind);
        write(writer);
        return Sha256Digest.Hash(writer.ToArray());
    }
}

public sealed class CanonicalFingerprintWriter
{
    private readonly List<byte> _bytes = [];

    internal CanonicalFingerprintWriter(string kind)
    {
        WriteInt32(CanonicalFingerprintEncodingV1.Version);
        Field(0, Encoding.UTF8.GetBytes(kind));
    }

    public void Utf8(int fieldId, string value) => Field(fieldId, Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC)));
    public void SignedNumber(int fieldId, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        Field(fieldId, bytes);
    }
    public void Boolean(int fieldId, bool value) => Field(fieldId, [value ? (byte)1 : (byte)0]);
    public void Digest(int fieldId, Sha256Digest value) => Field(fieldId, Convert.FromHexString(value.Value));

    public void SortedUtf8(int fieldId, IEnumerable<string> values)
    {
        foreach (var value in values.Order(StringComparer.Ordinal)) Utf8(fieldId, value);
    }

    internal byte[] ToArray() => [.. _bytes];

    private void Field(int fieldId, ReadOnlySpan<byte> value)
    {
        WriteInt32(fieldId);
        WriteInt32(value.Length);
        _bytes.AddRange(value.ToArray());
    }

    private void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _bytes.AddRange(bytes.ToArray());
    }
}

public sealed record CandidateComponentFingerprints(
    DiagnosticFingerprint Rules,
    DiagnosticFingerprint Boundary,
    DiagnosticFingerprint LinkPolicy,
    DiagnosticFingerprint ExternalMapping,
    DiagnosticFingerprint Format,
    DiagnosticFingerprint Compression,
    DiagnosticFingerprint Protection,
    DiagnosticFingerprint Manifest);

public sealed record CandidateArchiveFingerprints(
    int EncodingVersion,
    PortableSemanticsPins Semantics,
    bool ObservationComplete,
    EntrySetFingerprint EntrySet,
    SelectionFingerprint Selection,
    ArchiveSpecFingerprint ArchiveSpec,
    OutputLayoutFingerprint OutputLayout,
    ExecutionSemanticFingerprint ExecutionSemantic,
    ExecutionBindingFingerprint ExecutionBinding,
    CandidateComponentFingerprints Components);

public sealed class CommittedArchiveUnitBaseline
{
    private CommittedArchiveUnitBaseline(PlanId planId, ArchiveUnitId archiveUnitId, CandidateArchiveFingerprints fingerprints)
    {
        PlanId = planId;
        ArchiveUnitId = archiveUnitId;
        FingerprintEncodingVersion = fingerprints.EncodingVersion;
        Semantics = fingerprints.Semantics;
        EntrySet = fingerprints.EntrySet;
        Selection = fingerprints.Selection;
        ArchiveSpec = fingerprints.ArchiveSpec;
        OutputLayout = fingerprints.OutputLayout;
        Components = fingerprints.Components;
    }

    public PlanId PlanId { get; }
    public ArchiveUnitId ArchiveUnitId { get; }
    public int FingerprintEncodingVersion { get; }
    public PortableSemanticsPins Semantics { get; }
    public EntrySetFingerprint EntrySet { get; }
    public SelectionFingerprint Selection { get; }
    public ArchiveSpecFingerprint ArchiveSpec { get; }
    public OutputLayoutFingerprint OutputLayout { get; }
    public CandidateComponentFingerprints Components { get; }

    public static CommittedArchiveUnitBaseline FromPublishedCandidate(PlanId planId, ArchiveUnitId archiveUnitId, CandidateArchiveFingerprints fingerprints)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        if (!fingerprints.ObservationComplete) throw new InvalidOperationException("Preview-only fingerprints cannot become a committed baseline.");
        return new CommittedArchiveUnitBaseline(planId, archiveUnitId, fingerprints);
    }
}

public enum ArchiveChangeStatus { FirstBackup, Unchanged, RebuildRequired, BlockedByIncompleteSource }
public enum OutputLayoutChangeStatus { Unchanged, ReorganizationRequired, BlockedByIncompleteSource }
public enum ChangeReason
{
    NoBaseline, EntrySetChanged, RulesChanged, BoundaryChanged, LinkPolicyChanged, ExternalSourceChanged,
    ArchiveFormatChanged, CompressionChanged, EncryptionChanged, ManifestSchemaChanged,
    SemanticsVersionChanged, BaselineInvalid, IncompleteObservation
}

public sealed record ChangeDecision(
    ArchiveChangeStatus ArchiveChange,
    OutputLayoutChangeStatus OutputLayoutChange,
    ImmutableArray<ChangeReason> Reasons);

public static class ChangeDetector
{
    public static ChangeDecision Detect(CandidateArchiveFingerprints candidate, CommittedArchiveUnitBaseline? baseline)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.ObservationComplete)
            return new(ArchiveChangeStatus.BlockedByIncompleteSource, OutputLayoutChangeStatus.BlockedByIncompleteSource, [ChangeReason.IncompleteObservation]);
        if (baseline is null)
            return new(ArchiveChangeStatus.FirstBackup, OutputLayoutChangeStatus.Unchanged, [ChangeReason.NoBaseline]);
        if (candidate.EncodingVersion != CanonicalFingerprintEncodingV1.Version
            || baseline.FingerprintEncodingVersion != CanonicalFingerprintEncodingV1.Version
            || !PortableSemanticsSupport.Validate(candidate.Semantics).IsEmpty
            || !PortableSemanticsSupport.Validate(baseline.Semantics).IsEmpty)
            return new(ArchiveChangeStatus.RebuildRequired, OutputLayoutChangeStatus.ReorganizationRequired, [ChangeReason.BaselineInvalid]);

        // top-level fingerprint 是 equality authority；component fingerprints 只负责稳定诊断原因。
        var reasons = new List<ChangeReason>();
        if (candidate.EntrySet != baseline.EntrySet) reasons.Add(ChangeReason.EntrySetChanged);
        AddComponentReasons(candidate.Components, baseline.Components, reasons);
        if (candidate.Selection != baseline.Selection && reasons.Count == 0) reasons.Add(ChangeReason.SemanticsVersionChanged);
        if (candidate.ArchiveSpec != baseline.ArchiveSpec && !reasons.Any(IsArchiveSpecReason)) reasons.Add(ChangeReason.SemanticsVersionChanged);
        var rebuild = candidate.EntrySet != baseline.EntrySet || candidate.Selection != baseline.Selection || candidate.ArchiveSpec != baseline.ArchiveSpec;
        var reorganize = candidate.OutputLayout != baseline.OutputLayout;
        return new(
            rebuild ? ArchiveChangeStatus.RebuildRequired : ArchiveChangeStatus.Unchanged,
            reorganize ? OutputLayoutChangeStatus.ReorganizationRequired : OutputLayoutChangeStatus.Unchanged,
            [.. reasons.Distinct()]);
    }

    private static void AddComponentReasons(CandidateComponentFingerprints current, CandidateComponentFingerprints previous, List<ChangeReason> reasons)
    {
        if (current.Rules != previous.Rules) reasons.Add(ChangeReason.RulesChanged);
        if (current.Boundary != previous.Boundary) reasons.Add(ChangeReason.BoundaryChanged);
        if (current.LinkPolicy != previous.LinkPolicy) reasons.Add(ChangeReason.LinkPolicyChanged);
        if (current.ExternalMapping != previous.ExternalMapping) reasons.Add(ChangeReason.ExternalSourceChanged);
        if (current.Format != previous.Format) reasons.Add(ChangeReason.ArchiveFormatChanged);
        if (current.Compression != previous.Compression) reasons.Add(ChangeReason.CompressionChanged);
        if (current.Protection != previous.Protection) reasons.Add(ChangeReason.EncryptionChanged);
        if (current.Manifest != previous.Manifest) reasons.Add(ChangeReason.ManifestSchemaChanged);
    }

    private static bool IsArchiveSpecReason(ChangeReason reason) => reason is ChangeReason.ArchiveFormatChanged or ChangeReason.CompressionChanged or ChangeReason.EncryptionChanged or ChangeReason.ManifestSchemaChanged;
}

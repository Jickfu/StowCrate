using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Core.Tests.ChangeDetection;

public sealed class ChangeDetectorTests
{
    private static readonly PlanId PlanId = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly ArchiveUnitId UnitId = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));

    [Fact]
    public void CanonicalEncodingIsDeterministicLengthDelimitedAndKindSeparated()
    {
        var first = CanonicalFingerprintEncodingV1.Encode("entry", writer => { writer.Utf8(1, "ab"); writer.Utf8(2, "c"); });
        var same = CanonicalFingerprintEncodingV1.Encode("entry", writer => { writer.Utf8(1, "ab"); writer.Utf8(2, "c"); });
        var ambiguousWithoutLengths = CanonicalFingerprintEncodingV1.Encode("entry", writer => { writer.Utf8(1, "a"); writer.Utf8(2, "bc"); });
        var otherKind = CanonicalFingerprintEncodingV1.Encode("selection", writer => { writer.Utf8(1, "ab"); writer.Utf8(2, "c"); });

        Assert.Equal(first, same);
        Assert.NotEqual(first, ambiguousWithoutLengths);
        Assert.NotEqual(first, otherKind);
    }

    [Fact]
    public void CompressionChangeOnlyRebuildsWhileOutputChangeOnlyReorganizes()
    {
        var original = Fingerprints();
        var baseline = CommittedArchiveUnitBaseline.FromPublishedCandidate(PlanId, UnitId, original);
        var compression = original with
        {
            ArchiveSpec = ArchiveSpec("changed"),
            Components = original.Components with { Compression = Diagnostic("changed-compression") }
        };
        var output = original with { OutputLayout = Output("changed-output") };

        var compressionDecision = ChangeDetector.Detect(compression, baseline);
        var outputDecision = ChangeDetector.Detect(output, baseline);

        Assert.Equal(ArchiveChangeStatus.RebuildRequired, compressionDecision.ArchiveChange);
        Assert.Equal(OutputLayoutChangeStatus.Unchanged, compressionDecision.OutputLayoutChange);
        Assert.Contains(ChangeReason.CompressionChanged, compressionDecision.Reasons);
        Assert.Equal(ArchiveChangeStatus.Unchanged, outputDecision.ArchiveChange);
        Assert.Equal(OutputLayoutChangeStatus.ReorganizationRequired, outputDecision.OutputLayoutChange);
    }

    [Fact]
    public void FormatChangeRebuildsAndReorganizes()
    {
        var original = Fingerprints();
        var baseline = CommittedArchiveUnitBaseline.FromPublishedCandidate(PlanId, UnitId, original);
        var changed = original with
        {
            ArchiveSpec = ArchiveSpec("format"),
            OutputLayout = Output("extension"),
            Components = original.Components with { Format = Diagnostic("format") }
        };

        var decision = ChangeDetector.Detect(changed, baseline);

        Assert.Equal(ArchiveChangeStatus.RebuildRequired, decision.ArchiveChange);
        Assert.Equal(OutputLayoutChangeStatus.ReorganizationRequired, decision.OutputLayoutChange);
        Assert.Contains(ChangeReason.ArchiveFormatChanged, decision.Reasons);
    }

    [Fact]
    public void IncompleteAndUnknownEncodingNeverBecomeUnchangedOrCommitted()
    {
        var complete = Fingerprints();
        var incomplete = complete with { ObservationComplete = false };
        Assert.Throws<InvalidOperationException>(() => CommittedArchiveUnitBaseline.FromPublishedCandidate(PlanId, UnitId, incomplete));
        var blocked = ChangeDetector.Detect(incomplete, null);
        Assert.Equal(ArchiveChangeStatus.BlockedByIncompleteSource, blocked.ArchiveChange);

        var invalidBaseline = CommittedArchiveUnitBaseline.FromPublishedCandidate(PlanId, UnitId, complete with { EncodingVersion = 99 });
        var invalid = ChangeDetector.Detect(complete, invalidBaseline);
        Assert.Equal(ArchiveChangeStatus.RebuildRequired, invalid.ArchiveChange);
        Assert.Contains(ChangeReason.BaselineInvalid, invalid.Reasons);
    }

    private static CandidateArchiveFingerprints Fingerprints()
    {
        var diagnostic = Diagnostic("same");
        return new CandidateArchiveFingerprints(
            1, new PortableSemanticsPins(1, 1, 1), true, new EntrySetFingerprint(Digest("entry")), new SelectionFingerprint(Digest("selection")),
            ArchiveSpec("archive"), Output("output"), new ExecutionSemanticFingerprint(Digest("semantic")),
            new ExecutionBindingFingerprint(Digest("binding")),
            new CandidateComponentFingerprints(diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic, diagnostic));
    }

    private static Sha256Digest Digest(string value) => CanonicalFingerprintEncodingV1.Encode("test", writer => writer.Utf8(1, value));
    private static DiagnosticFingerprint Diagnostic(string value) => new(Digest(value));
    private static ArchiveSpecFingerprint ArchiveSpec(string value) => new(Digest(value));
    private static OutputLayoutFingerprint Output(string value) => new(Digest(value));
}

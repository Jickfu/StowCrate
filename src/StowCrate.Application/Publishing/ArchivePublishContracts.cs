using System.Collections.Immutable;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Paths;

namespace StowCrate.Application.Publishing;

public static class HistoryPhysicalLayoutV1
{
    public const int SemanticsVersion = 1;

    public static RelativeStoragePath Create(ArchiveUnitId unitId, ArchiveVersion oldArchive)
    {
        if (oldArchive.PublishedAtUtc is null) throw new ArgumentException("Old archive must have a publish timestamp.", nameof(oldArchive));
        var extension = oldArchive.ArchiveFormat switch
        {
            PortableArchiveFormat.SevenZip => ".7z",
            PortableArchiveFormat.Zip => ".zip",
            PortableArchiveFormat.TarZstd => ".tar.zst",
            _ => throw new ArgumentOutOfRangeException(nameof(oldArchive))
        };
        var stamp = oldArchive.PublishedAtUtc.Value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'.'fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return new($"history-v1/{unitId.Value:D}/{stamp}--{oldArchive.Id.Value:D}{extension}");
    }
}

public static class CurrentPublishTempLayoutV1
{
    public static RelativeStoragePath Create(RelativeStoragePath finalPath, ArchiveVersionId versionId)
    {
        var slash = finalPath.Value.LastIndexOf('/');
        var directory = slash < 0 ? "" : finalPath.Value[..(slash + 1)];
        var fileName = slash < 0 ? finalPath.Value : finalPath.Value[(slash + 1)..];
        return new($"{directory}.{fileName}.stowcrate-publish-{versionId.Value:N}.partial");
    }
}

public sealed record ArchivePublishRequest
{
    public ArchivePublishRequest(VerifiedArchiveArtifact artifact, BaselineCandidate baselineCandidate,
        OutputLayoutFingerprint outputLayoutFingerprint, LogicalPath frozenCandidateOutputRelativePath, RelativeStoragePath currentRelativePath,
        EffectiveHistoryPolicy historyPolicy, ExecutionSemanticSnapshot capturedExecutionSnapshot,
        OutputRootLocalBinding currentRoot, OutputRootLocalBinding? historyRoot)
    {
        ArgumentNullException.ThrowIfNull(artifact); ArgumentNullException.ThrowIfNull(baselineCandidate);
        ArgumentNullException.ThrowIfNull(historyPolicy); ArgumentNullException.ThrowIfNull(capturedExecutionSnapshot);
        ArgumentNullException.ThrowIfNull(currentRoot);
        var version = artifact.ArchiveVersion;
        if (version.PlanId != artifact.Manifest.PlanId || version.ArchiveUnitId != artifact.Manifest.ArchiveUnitId)
            throw new ArgumentException("Artifact and candidate identities differ.", nameof(artifact));
        if (version.ArchiveSpecFingerprint != baselineCandidate.Fingerprints.ArchiveSpec || outputLayoutFingerprint != baselineCandidate.Fingerprints.OutputLayout)
            throw new ArgumentException("Artifact/output fingerprints must match the frozen candidate.", nameof(baselineCandidate));
        if (currentRelativePath.Value != frozenCandidateOutputRelativePath.Value)
            throw new ArgumentException("Current path must be the candidate's frozen OutputRelativePath.", nameof(currentRelativePath));
        if (version.Lifecycle is not ArchiveVersionLifecycle.Verified) throw new ArgumentException("Artifact must remain Verified.", nameof(artifact));
        if (capturedExecutionSnapshot.PlanId != version.PlanId || !capturedExecutionSnapshot.Units.ContainsKey(version.ArchiveUnitId))
            throw new ArgumentException("Captured execution snapshot does not contain the artifact unit.", nameof(capturedExecutionSnapshot));
        if (historyPolicy is EffectiveHistoryEnabled && historyRoot is null) throw new ArgumentException("HistoryRoot is required when History is enabled.", nameof(historyRoot));
        Artifact = artifact; BaselineCandidate = baselineCandidate; OutputLayoutFingerprint = outputLayoutFingerprint;
        CurrentRelativePath = currentRelativePath; HistoryPolicy = historyPolicy; CapturedExecutionSnapshot = capturedExecutionSnapshot;
        CurrentRoot = currentRoot; HistoryRoot = historyRoot;
    }
    public VerifiedArchiveArtifact Artifact { get; }
    public BaselineCandidate BaselineCandidate { get; }
    public OutputLayoutFingerprint OutputLayoutFingerprint { get; }
    public RelativeStoragePath CurrentRelativePath { get; }
    public EffectiveHistoryPolicy HistoryPolicy { get; }
    public ExecutionSemanticSnapshot CapturedExecutionSnapshot { get; }
    public OutputRootLocalBinding CurrentRoot { get; }
    public OutputRootLocalBinding? HistoryRoot { get; }
}

public sealed record PhysicalArchiveObservation(RelativeStoragePath RelativeStoragePath, Sha256Digest Sha256, long Length);
public sealed record CurrentPublishStagingProof(PlanId PlanId, ArchiveUnitId ArchiveUnitId, ArchiveVersionId ArchiveVersionId,
    RelativeStoragePath RelativeStoragePath, Sha256Digest ExpectedSha256, Sha256Digest ObservedSha256, long Length);
public sealed record HistoryCapturePhysicalProof(PlanId PlanId, ArchiveUnitId ArchiveUnitId, ArchiveVersionId ArchiveVersionId,
    RelativeStoragePath RelativeStoragePath, Sha256Digest ExpectedSha256, Sha256Digest ObservedSha256, long Length);
public sealed record CurrentPublishReceipt(PlanId PlanId, ArchiveUnitId ArchiveUnitId, ArchiveVersionId ArchiveVersionId,
    RelativeStoragePath RelativeStoragePath, Sha256Digest ExpectedSha256, Sha256Digest ObservedSha256, long Length, DateTimeOffset PublishedAtUtc);

public interface IArchivePhysicalPublisher
{
    Task<PhysicalArchiveObservation?> ObserveAsync(OutputRootLocalBinding root, RelativeStoragePath path, CancellationToken cancellationToken);
    Task<CurrentPublishStagingProof> StageCurrentAsync(ArchivePublishRequest request, CancellationToken cancellationToken);
    Task<HistoryCapturePhysicalProof> CaptureHistoryAsync(OldCurrentFacts oldCurrent, OutputRootLocalBinding currentRoot,
        OutputRootLocalBinding historyRoot, RelativeStoragePath historyPath, CancellationToken cancellationToken);
    Task<CurrentPublishReceipt> PublishCurrentAsync(ArchivePublishRequest request, CurrentPublishStagingProof staging,
        OldCurrentFacts? oldCurrent, CancellationToken cancellationToken);
    Task<bool> DeleteIfMatchesAsync(OutputRootLocalBinding root, RelativeStoragePath path, Sha256Digest expected, long length, CancellationToken cancellationToken);
    Task CleanupRuntimeArtifactAsync(string path, CancellationToken cancellationToken);
}

public interface ICurrentExecutionSemanticSnapshotProvider
{
    Task<ExecutionSemanticSnapshot> LoadCurrentAsync(PlanId planId, CancellationToken cancellationToken);
}

public enum ArchivePublishFailureCode
{
    UnfinishedPublishIntent, CurrentFilesystemStateConflict, UnexpectedCurrentArtifact,
    PhysicalPublishFailed, PlanChangedDuringRun, AmbiguousPublishRecovery, MetadataCommitFailed
}

public sealed record ArchivePublishResult(DurableUnitMetadataCommitResult? Commit, ArchivePublishFailureCode? Failure,
    bool SkipRetentionCleanup, ImmutableArray<string> Warnings)
{
    public bool Succeeded => Commit is not null;
}

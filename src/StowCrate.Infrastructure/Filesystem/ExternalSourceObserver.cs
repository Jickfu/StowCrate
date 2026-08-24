using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Planning;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Infrastructure.Filesystem;

public sealed class ExternalSourceObserver(SourceScanner directoryScanner, IPhysicalFileSystem fileSystem)
{
    public ObservationResult<ExternalSourceSnapshot> Observe(
        ResolvedExternalSource external,
        SourceScanOptions? directoryOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(external);
        return external.Kind switch
        {
            PortableExternalSourceKind.File => ObserveFile(external, directoryOptions, cancellationToken),
            PortableExternalSourceKind.Directory => ObserveDirectory(external, directoryOptions, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown External Source kind {external.Kind}.")
        };
    }

    private ObservationResult<ExternalSourceSnapshot> ObserveFile(
        ResolvedExternalSource external,
        SourceScanOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physical = fileSystem.Inspect(external.PhysicalInput.CanonicalPath);
            if (physical.Kind is not FileSystemEntryKind.File)
            {
                return Failure(external.ExternalSourceId, "EXTOBS0001", "External File binding must resolve to a real regular file; links and other kinds are rejected.");
            }

            var legacy = new SourceEntry(
                new LogicalPath("file"),
                FileSystemEntryKind.File,
                physical.Length,
                lastWriteTimeUtc: physical.LastWriteTimeUtc,
                metadataFlags: physical.MetadataFlags);
            options ??= new SourceScanOptions();
            var contentIdentity = options.ComputeFullContentHashes
                ? ObservedContentIdentity.FullSha256(new Sha256Digest(fileSystem.ComputeSha256(external.PhysicalInput.CanonicalPath, cancellationToken)))
                : ObservedContentIdentity.MetadataV1;
            var rootEntry = new ObservedFileSystemEntry(
                LogicalPath.Root,
                legacy.Kind,
                legacy.Length,
                null,
                contentIdentity,
                null,
                legacy.LastWriteTimeUtc,
                null,
                legacy.MetadataFlags);
            var snapshot = new ExternalSourceSnapshot(
                external.ExternalSourceId,
                ExternalObservedRootKind.File,
                [rootEntry],
                [],
                ObservationCompleteness.Complete);
            return new ObservationResult<ExternalSourceSnapshot>(snapshot, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Failure(external.ExternalSourceId, "EXTOBS0002", $"External File cannot be observed: {exception.Message}");
        }
    }

    private ObservationResult<ExternalSourceSnapshot> ObserveDirectory(
        ResolvedExternalSource external,
        SourceScanOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new SourceScanOptions();
        options = options with { ObserveBackupIgnoreRuleSource = false };
        var legacySource = new BackupSource(external.ExternalSourceId.Value.ToString("D"), "external");
        var scanned = directoryScanner.Scan(
            legacySource,
            external.PhysicalInput.CanonicalPath,
            options,
            cancellationToken);
        var issues = scanned.Issues.Select(issue => new ObservationIssue(
            issue.Severity switch
            {
                ScanIssueSeverity.Info => ObservationIssueSeverity.Info,
                ScanIssueSeverity.Warning => ObservationIssueSeverity.Warning,
                ScanIssueSeverity.Fatal => ObservationIssueSeverity.Fatal,
                _ => throw new InvalidOperationException($"Unknown scan severity {issue.Severity}.")
            },
            $"EXT{issue.Code}",
            issue.Path,
            issue.Message)).ToArray();
        if (scanned.Snapshot is null)
        {
            return new ObservationResult<ExternalSourceSnapshot>(null, issues);
        }

        var completeness = issues.Any(issue => issue.Severity is ObservationIssueSeverity.Warning or ObservationIssueSeverity.Fatal)
            ? ObservationCompleteness.Incomplete
            : ObservationCompleteness.Complete;
        var snapshot = new ExternalSourceSnapshot(
            external.ExternalSourceId,
            ExternalObservedRootKind.Directory,
            scanned.Snapshot.Entries.Select(TypedSourceObserver.MapEntry),
            issues,
            completeness);
        return new ObservationResult<ExternalSourceSnapshot>(snapshot, issues);
    }

    private static ObservationResult<ExternalSourceSnapshot> Failure(
        ExternalSourceId id,
        string code,
        string message)
    {
        var issue = new ObservationIssue(ObservationIssueSeverity.Fatal, code, null, message);
        return new ObservationResult<ExternalSourceSnapshot>(null, [issue]);
    }
}

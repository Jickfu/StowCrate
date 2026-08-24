using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Planning;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Infrastructure.Filesystem;

public sealed class TypedSourceObserver(SourceScanner scanner)
{
    public ObservationResult<SourceObservationSnapshot> Observe(
        ResolvedBackupSource source,
        SourceScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var legacySource = new BackupSource(source.SourceId.Value.ToString("D"), "source");
        var scanned = scanner.Scan(
            legacySource,
            source.PhysicalRoot.CanonicalPath,
            options,
            cancellationToken);
        var issues = scanned.Issues.Select(MapIssue).ToArray();
        if (scanned.Snapshot is null)
        {
            return new ObservationResult<SourceObservationSnapshot>(null, issues);
        }

        var completeness = issues.Any(issue => issue.Severity is ObservationIssueSeverity.Warning or ObservationIssueSeverity.Fatal)
            ? ObservationCompleteness.Incomplete
            : ObservationCompleteness.Complete;
        var snapshot = new SourceObservationSnapshot(
            source.SourceId,
            scanned.Snapshot.FileSystemCaseSensitivity,
            scanned.Snapshot.Entries.Select(MapEntry),
            issues,
            completeness);
        return new ObservationResult<SourceObservationSnapshot>(snapshot, issues);
    }

    internal static ObservedFileSystemEntry MapEntry(SourceEntry entry) => new(
        entry.Path,
        entry.Kind,
        entry.Length,
        entry.TextContent,
        entry.FullContentSha256 is null
            ? ObservedContentIdentity.MetadataV1
            : ObservedContentIdentity.FullSha256(new Sha256Digest(entry.FullContentSha256)),
        entry.RawFileSha256 is null ? null : new Sha256Digest(entry.RawFileSha256),
        entry.LastWriteTimeUtc,
        entry.Link,
        entry.MetadataFlags);

    private static ObservationIssue MapIssue(ScanIssue issue) => new(
        issue.Severity switch
        {
            ScanIssueSeverity.Info => ObservationIssueSeverity.Info,
            ScanIssueSeverity.Warning => ObservationIssueSeverity.Warning,
            ScanIssueSeverity.Fatal => ObservationIssueSeverity.Fatal,
            _ => throw new InvalidOperationException($"Unknown scan severity {issue.Severity}.")
        },
        issue.Code,
        issue.Path,
        issue.Message);
}

using System.Collections.ObjectModel;
using StowCrate.Core.Paths;
using StowCrate.Core.Planning;

namespace StowCrate.Core.Filesystem;

public enum ScanIssueSeverity
{
    Info,
    Warning,
    Fatal,
}

public sealed record ScanIssue(
    ScanIssueSeverity Severity,
    string Code,
    LogicalPath? Path,
    string Message);

public sealed class SourceScanResult
{
    public SourceScanResult(SourceSnapshot? snapshot, IEnumerable<ScanIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        var frozenIssues = issues
            .OrderBy(issue => issue.Path?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ToArray();
        if ((snapshot is null) != frozenIssues.Any(issue => issue.Severity is ScanIssueSeverity.Fatal))
        {
            throw new ArgumentException("Fatal 扫描结果不能携带快照；非 Fatal 结果必须携带快照。", nameof(snapshot));
        }

        Snapshot = snapshot;
        Issues = new ReadOnlyCollection<ScanIssue>(frozenIssues);
    }

    public SourceSnapshot? Snapshot { get; }

    public IReadOnlyList<ScanIssue> Issues { get; }

    public bool IsSuccess => Snapshot is not null;

    public bool HasWarnings => Issues.Any(issue => issue.Severity is ScanIssueSeverity.Warning);
}

using StowCrate.Core.Paths;
using StowCrate.Core.Planning;
using StowCrate.Core.Rules;

namespace StowCrate.Core.Tests.Planning;

public sealed class ArchivePlannerTests
{
    [Fact]
    public void NestedArchiveUnitsProduceDeterministicBoundaryAwarePlan()
    {
        var source = new BackupSource("source-a", "A");
        var snapshot = Snapshot(
            source,
            Directory("B"),
            File("B/.backupignore", text: string.Empty),
            File("B/keep.txt", length: 10),
            File("B/drop.log", length: 20),
            Directory("C"),
            Directory("C/D"),
            File("C/D/.backupignore", text: "!F/**"),
            Directory("C/D/E"),
            File("C/D/E/d.txt", length: 30),
            Directory("C/D/F"),
            File("C/D/F/.backupignore", text: "@mode include-only\n!keep.txt"),
            File("C/D/F/keep.txt", length: 40),
            File("C/D/F/drop.txt", length: 50));
        var backupPlan = new BackupPlan(
            "plan-a",
            source,
            globalRules: [new BackupRule(RuleAction.Exclude, "*.log")]);

        var plan = SuccessfulPlan(backupPlan, snapshot);

        Assert.Equal("B.7z|C/D.7z|C/D/F.7z", string.Join('|', plan.Archives.Select(archive => archive.OutputPath.Value)));
        Assert.Equal(
            ".backupignore|keep.txt",
            string.Join('|', Archive(plan, "B.7z").Entries.Select(entry => entry.ArchivePath.Value)));
        Assert.Equal(
            ".backupignore|E|E/d.txt",
            string.Join('|', Archive(plan, "C/D.7z").Entries.Select(entry => entry.ArchivePath.Value)));
        Assert.Equal(
            ".backupignore|keep.txt",
            string.Join('|', Archive(plan, "C/D/F.7z").Entries.Select(entry => entry.ArchivePath.Value)));
        Assert.DoesNotContain(
            Archive(plan, "C/D.7z").Entries,
            entry => entry.SourcePath.IsSameOrDescendantOf(new LogicalPath("C/D/F")));
    }

    [Fact]
    public void ParentIncludeRuleCannotCrossArchiveBoundary()
    {
        var source = new BackupSource("source", "A");
        var snapshot = Snapshot(
            source,
            Directory("D"),
            File("D/.backupignore", text: "!F/**"),
            Directory("D/F"),
            File("D/F/.backupignore", text: string.Empty),
            File("D/F/secret.txt"));

        var plan = SuccessfulPlan(new BackupPlan("plan", source), snapshot);

        Assert.DoesNotContain(Archive(plan, "D.7z").Entries, entry => entry.ArchivePath.Value.StartsWith('F'));
        Assert.Contains(Archive(plan, "D/F.7z").Entries, entry => entry.ArchivePath.Value == "secret.txt");
    }

    [Fact]
    public void ParentLocalRulesDoNotFlowIntoChildUnit()
    {
        var source = new BackupSource("source", "A");
        var snapshot = Snapshot(
            source,
            Directory("D"),
            File("D/.backupignore", text: "*.txt"),
            File("D/parent.txt"),
            Directory("D/F"),
            File("D/F/.backupignore", text: string.Empty),
            File("D/F/child.txt"));

        var plan = SuccessfulPlan(new BackupPlan("plan", source), snapshot);

        Assert.DoesNotContain(Archive(plan, "D.7z").Entries, entry => entry.ArchivePath.Value == "parent.txt");
        Assert.Contains(Archive(plan, "D/F.7z").Entries, entry => entry.ArchivePath.Value == "child.txt");
    }

    [Fact]
    public void OwnBackupIgnoreIsIncludedEvenWhenRulesExcludeEverything()
    {
        var source = new BackupSource("source", "Project");
        var snapshot = Snapshot(
            source,
            Directory("Project"),
            File("Project/.backupignore", text: "*"),
            File("Project/file.txt"));

        var plan = SuccessfulPlan(new BackupPlan("plan", source), snapshot);

        var entry = Assert.Single(Assert.Single(plan.Archives).Entries);
        Assert.Equal(".backupignore", entry.ArchivePath.Value);
    }

    [Fact]
    public void UiManagedAndFileManagedAtSameRootIsFatalConflict()
    {
        var source = new BackupSource("source", "Project");
        var snapshot = Snapshot(
            source,
            Directory("Project"),
            File("Project/.backupignore", text: string.Empty));
        var backupPlan = new BackupPlan(
            "plan",
            source,
            archiveUnits: [new ArchiveUnitDefinition(new LogicalPath("Project"), new RuleSet())]);

        var result = ArchivePlanner.CreatePlan(backupPlan, snapshot);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "RULE_SOURCE_CONFLICT");
    }

    [Fact]
    public void ReservedArchiveNamespaceConflictIsFatal()
    {
        var source = new BackupSource("source", "Project");
        var snapshot = Snapshot(
            source,
            Directory("Project"),
            File("Project/.backupignore", text: string.Empty),
            Directory("Project/__stowcrate__"),
            File("Project/__stowcrate__/user-file.txt"));

        var result = ArchivePlanner.CreatePlan(new BackupPlan("plan", source), snapshot);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "RESERVED_NAMESPACE_CONFLICT");
    }

    [Fact]
    public void PlanOutputAndFingerprintDoNotDependOnSnapshotInputOrder()
    {
        var source = new BackupSource("source", "Project");
        SourceEntry[] entries =
        [
            Directory("Project"),
            File("Project/.backupignore", text: string.Empty),
            File("Project/b.txt", length: 2),
            File("Project/a.txt", length: 1),
        ];
        var backupPlan = new BackupPlan("plan", source);

        var first = SuccessfulPlan(backupPlan, Snapshot(source, entries));
        var second = SuccessfulPlan(backupPlan, Snapshot(source, entries.Reverse().ToArray()));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            first.Archives.SelectMany(archive => archive.Entries).Select(entry => entry.ArchivePath.Value),
            second.Archives.SelectMany(archive => archive.Entries).Select(entry => entry.ArchivePath.Value));
    }

    [Fact]
    public void BackupIgnoreCommentChangeChangesPlanFingerprint()
    {
        var source = new BackupSource("source", "Project");
        var first = Snapshot(
            source,
            Directory("Project"),
            File("Project/.backupignore", text: "# first"),
            File("Project/a.txt"));
        var second = Snapshot(
            source,
            Directory("Project"),
            File("Project/.backupignore", text: "# second"),
            File("Project/a.txt"));
        var backupPlan = new BackupPlan("plan", source);

        var firstPlan = SuccessfulPlan(backupPlan, first);
        var secondPlan = SuccessfulPlan(backupPlan, second);

        Assert.NotEqual(firstPlan.Fingerprint, secondPlan.Fingerprint);
    }

    [Fact]
    public void BoundaryTreeChangeChangesParentFingerprint()
    {
        var source = new BackupSource("source", "A");
        var withoutChild = Snapshot(
            source,
            Directory("D"),
            File("D/.backupignore", text: string.Empty),
            Directory("D/F"),
            File("D/F/file.txt"));
        var withChild = Snapshot(
            source,
            Directory("D"),
            File("D/.backupignore", text: string.Empty),
            Directory("D/F"),
            File("D/F/.backupignore", text: string.Empty),
            File("D/F/file.txt"));
        var backupPlan = new BackupPlan("plan", source);

        var firstFingerprint = Archive(SuccessfulPlan(backupPlan, withoutChild), "D.7z").Fingerprint;
        var secondFingerprint = Archive(SuccessfulPlan(backupPlan, withChild), "D.7z").Fingerprint;

        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void MissingArchiveUnitsIsFatal()
    {
        var source = new BackupSource("source", "A");
        var snapshot = Snapshot(source, File("unboxed.txt"));

        var result = ArchivePlanner.CreatePlan(new BackupPlan("plan", source), snapshot);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "NO_ARCHIVE_UNIT");
    }

    [Fact]
    public void BoundaryDiscoveryIgnoresFilteringRules()
    {
        var source = new BackupSource("source", "A");
        var snapshot = Snapshot(
            source,
            Directory("node_modules"),
            Directory("node_modules/X"),
            File("node_modules/X/.backupignore", text: string.Empty),
            File("node_modules/X/keep.txt"));
        var backupPlan = new BackupPlan(
            "plan",
            source,
            globalRules: [new BackupRule(RuleAction.Exclude, "node_modules/")]);

        var plan = SuccessfulPlan(backupPlan, snapshot);

        var archive = Assert.Single(plan.Archives);
        Assert.Equal("node_modules/X.7z", archive.OutputPath.Value);
        Assert.Contains(archive.Entries, entry => entry.ArchivePath.Value == "keep.txt");
    }

    [Fact]
    public void UiManagedUnitUsesItsCompleteLocalRuleSet()
    {
        var source = new BackupSource("source", "A");
        var snapshot = Snapshot(
            source,
            Directory("Project"),
            File("Project/keep.txt"),
            File("Project/drop.txt"));
        var localRules = new RuleSet(
            RuleMode.IncludeOnly,
            CaseSensitivity.Sensitive,
            [new BackupRule(RuleAction.Include, "keep.txt")]);
        var backupPlan = new BackupPlan(
            "plan",
            source,
            archiveUnits: [new ArchiveUnitDefinition(new LogicalPath("Project"), localRules)]);

        var plan = SuccessfulPlan(backupPlan, snapshot);

        var archive = Assert.Single(plan.Archives);
        Assert.Equal(RuleSource.UiManaged, archive.ArchiveUnit.RuleSource);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("keep.txt", entry.ArchivePath.Value);
    }

    private static ArchivePlan SuccessfulPlan(BackupPlan backupPlan, SourceSnapshot snapshot)
    {
        var result = ArchivePlanner.CreatePlan(backupPlan, snapshot);
        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        return Assert.IsType<ArchivePlan>(result.Plan);
    }

    private static PlannedArchive Archive(ArchivePlan plan, string outputPath)
    {
        return Assert.Single(plan.Archives, archive => archive.OutputPath.Value == outputPath);
    }

    private static SourceSnapshot Snapshot(BackupSource source, params SourceEntry[] entries)
    {
        return new SourceSnapshot(source, CaseSensitivity.Sensitive, entries);
    }

    private static SourceEntry Directory(string path)
    {
        return new SourceEntry(new LogicalPath(path), SourceEntryKind.Directory);
    }

    private static SourceEntry File(string path, long length = 0, string? text = null)
    {
        return new SourceEntry(new LogicalPath(path), SourceEntryKind.File, length, text);
    }
}

using StowCrate.Core.Filesystem;
using StowCrate.Core.Planning;
using StowCrate.Core.Rules;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed class SourceScannerTests
{
    [Fact]
    public void RealFileSystemProducesSnapshotConsumableByPlanningKernel()
    {
        using var fixture = new TemporaryDirectory();
        var project = Directory.CreateDirectory(Path.Combine(fixture.Path, "Project"));
        File.WriteAllText(Path.Combine(project.FullName, ".backupignore"), "*.tmp");
        File.WriteAllText(Path.Combine(project.FullName, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(project.FullName, "drop.tmp"), "drop");
        var source = new BackupSource("source", "Root");

        var scan = new SourceScanner().Scan(source, fixture.Path);
        var planning = ArchivePlanner.CreatePlan(new BackupPlan("plan", source), Assert.IsType<SourceSnapshot>(scan.Snapshot));

        Assert.True(scan.IsSuccess);
        Assert.Empty(scan.Issues);
        Assert.True(planning.IsSuccess);
        var archive = Assert.Single(planning.Plan!.Archives);
        Assert.Contains(archive.Entries, entry => entry.ArchivePath.Value == "keep.txt");
        Assert.DoesNotContain(archive.Entries, entry => entry.ArchivePath.Value == "drop.tmp");
    }

    [Fact]
    public void LinkIsCapturedButNeverEnumerated()
    {
        var fileSystem = FakePhysicalFileSystem.Create();
        fileSystem.AddLink("shared", "../outside", isDirectory: true);
        fileSystem.AddFile("shared/should-not-be-seen.txt");

        var result = Scan(fileSystem);

        var link = Assert.Single(result.Snapshot!.Entries);
        Assert.Equal(FileSystemEntryKind.Link, link.Kind);
        Assert.Equal("../outside", link.Link!.Target);
        Assert.DoesNotContain(fileSystem.EnumeratedDirectories, path => path.EndsWith("shared", StringComparison.Ordinal));
    }

    [Fact]
    public void BackupIgnoreLinkIsFatal()
    {
        var fileSystem = FakePhysicalFileSystem.Create();
        fileSystem.AddLink("Project/.backupignore", "../../rules", isDirectory: false);

        var result = Scan(fileSystem);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "SCFS0004" && issue.Severity is ScanIssueSeverity.Fatal);
    }

    [Fact]
    public void InvalidUtf8BackupIgnoreIsFatal()
    {
        using var fixture = new TemporaryDirectory();
        var project = Directory.CreateDirectory(Path.Combine(fixture.Path, "Project"));
        File.WriteAllBytes(Path.Combine(project.FullName, ".backupignore"), [0xff, 0xfe]);

        var result = new SourceScanner().Scan(new BackupSource("source", "Root"), fixture.Path);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "SCFS0005");
    }

    [Fact]
    public void PayloadModeDoesNotInterpretBackupIgnore()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(fixture.Path, ".backupignore"), [0xff, 0xfe]);

        var result = new SourceScanner().Scan(
            new BackupSource("external", "External"),
            fixture.Path,
            new SourceScanOptions(ObserveBackupIgnoreRuleSource: false));

        Assert.True(result.IsSuccess);
        var marker = Assert.Single(result.Snapshot!.Entries);
        Assert.Equal(".backupignore", marker.Path.Value);
        Assert.Null(marker.TextContent);
    }

    [Fact]
    public void EntryDisappearingDuringScanProducesWarningAndContinues()
    {
        var fileSystem = FakePhysicalFileSystem.Create();
        fileSystem.AddFile("gone.txt");
        fileSystem.ThrowOnInspect("gone.txt", new FileNotFoundException("gone"));
        fileSystem.AddFile("kept.txt");

        var result = Scan(fileSystem);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "SCFS1002");
        Assert.Contains(result.Snapshot!.Entries, entry => entry.Path.Value == "kept.txt");
        Assert.DoesNotContain(result.Snapshot.Entries, entry => entry.Path.Value == "gone.txt");
    }

    [Fact]
    public void InaccessibleSubdirectoryProducesWarningAndContinues()
    {
        var fileSystem = FakePhysicalFileSystem.Create();
        fileSystem.AddDirectory("private");
        fileSystem.ThrowOnEnumerate("private", new UnauthorizedAccessException("denied"));
        fileSystem.AddFile("public.txt");

        var result = Scan(fileSystem);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "SCFS1001" && issue.Path?.Value == "private");
        Assert.Contains(result.Snapshot!.Entries, entry => entry.Path.Value == "public.txt");
    }

    [Fact]
    public void DifferentFileSystemStopsAtBoundary()
    {
        var fileSystem = FakePhysicalFileSystem.Create();
        fileSystem.AddDirectory("mounted", fileSystemId: "other");
        fileSystem.AddFile("mounted/hidden.txt", fileSystemId: "other");

        var result = Scan(fileSystem);

        Assert.Contains(result.Issues, issue => issue.Code == "SCFS1005");
        Assert.Contains(result.Snapshot!.Entries, entry => entry.Path.Value == "mounted");
        Assert.DoesNotContain(result.Snapshot.Entries, entry => entry.Path.Value == "mounted/hidden.txt");
    }

    [Fact]
    public void SpecialEntryIsVisibleButNotPlanned()
    {
        var fileSystem = FakePhysicalFileSystem.Create();
        fileSystem.AddSpecial("socket");

        var result = Scan(fileSystem);

        Assert.Contains(result.Issues, issue => issue.Code == "SCFS1004");
        Assert.Equal(FileSystemEntryKind.Special, Assert.Single(result.Snapshot!.Entries).Kind);
    }

    [Fact]
    public void CancellationIsNotConvertedToScanIssue()
    {
        var fileSystem = FakePhysicalFileSystem.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Scan(fileSystem, cancellation.Token));
    }

    [Fact]
    public void RealDirectorySymlinkPreservesRawTargetAndDoesNotFollow()
    {
        using var fixture = new TemporaryDirectory();
        var target = Directory.CreateDirectory(Path.Combine(fixture.Path, "target"));
        File.WriteAllText(Path.Combine(target.FullName, "target.txt"), "data");
        var linkPath = Path.Combine(fixture.Path, "link");
        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, "target");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var result = new SourceScanner().Scan(new BackupSource("source", "Root"), fixture.Path);

        var link = Assert.Single(result.Snapshot!.Entries, entry => entry.Path.Value == "link");
        Assert.Equal(FileSystemEntryKind.Link, link.Kind);
        Assert.Equal("target", link.Link!.Target);
        Assert.DoesNotContain(result.Snapshot.Entries, entry => entry.Path.Value == "link/target.txt");
    }

    private static SourceScanResult Scan(FakePhysicalFileSystem fileSystem, CancellationToken cancellationToken = default)
    {
        return new SourceScanner(fileSystem).Scan(
            new BackupSource("source", "Root"),
            fileSystem.Root,
            cancellationToken: cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stowcrate-tests-{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FakePhysicalFileSystem : IPhysicalFileSystem
    {
        private readonly Dictionary<string, PhysicalFileSystemEntry> _entries = new(PathComparer);
        private readonly Dictionary<string, Exception> _inspectFailures = new(PathComparer);
        private readonly Dictionary<string, Exception> _enumerationFailures = new(PathComparer);

        private FakePhysicalFileSystem(string root)
        {
            Root = root;
            _entries.Add(root, Entry(root, FileSystemEntryKind.Directory, "source"));
        }

        public string Root { get; }

        public List<string> EnumeratedDirectories { get; } = [];

        private static StringComparer PathComparer => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        public static FakePhysicalFileSystem Create()
        {
            return new FakePhysicalFileSystem(Path.Combine(Path.GetTempPath(), $"stowcrate-fake-{Guid.NewGuid():N}"));
        }

        public PhysicalFileSystemEntry Inspect(string path)
        {
            path = Path.GetFullPath(path);
            if (_inspectFailures.TryGetValue(path, out var exception))
            {
                throw exception;
            }

            return _entries[path];
        }

        public IEnumerable<string> EnumerateChildren(string directoryPath)
        {
            directoryPath = Path.GetFullPath(directoryPath);
            EnumeratedDirectories.Add(directoryPath);
            if (_enumerationFailures.TryGetValue(directoryPath, out var exception))
            {
                throw exception;
            }

            return _entries.Keys.Where(path => IsDirectChild(path, directoryPath)).ToArray();
        }

        public string ReadAllText(string path)
        {
            return string.Empty;
        }

        public void AddDirectory(string relativePath, string fileSystemId = "source")
        {
            AddParents(relativePath);
            var path = FullPath(relativePath);
            _entries[path] = Entry(path, FileSystemEntryKind.Directory, fileSystemId);
        }

        public void AddFile(string relativePath, string fileSystemId = "source")
        {
            AddParents(relativePath);
            var path = FullPath(relativePath);
            _entries[path] = Entry(path, FileSystemEntryKind.File, fileSystemId, length: 1);
        }

        public void AddLink(string relativePath, string target, bool isDirectory)
        {
            AddParents(relativePath);
            var path = FullPath(relativePath);
            _entries[path] = new PhysicalFileSystemEntry(
                path,
                FileSystemEntryKind.Link,
                0,
                DateTimeOffset.UnixEpoch,
                LinkKind.SymbolicLink,
                target,
                isDirectory,
                isDirectory ? SourceMetadata.DirectoryTarget : SourceMetadata.None,
                "source");
        }

        public void AddSpecial(string relativePath)
        {
            AddParents(relativePath);
            var path = FullPath(relativePath);
            _entries[path] = Entry(path, FileSystemEntryKind.Special, "source");
        }

        public void ThrowOnInspect(string relativePath, Exception exception)
        {
            _inspectFailures[FullPath(relativePath)] = exception;
        }

        public void ThrowOnEnumerate(string relativePath, Exception exception)
        {
            _enumerationFailures[FullPath(relativePath)] = exception;
        }

        private static PhysicalFileSystemEntry Entry(string path, FileSystemEntryKind kind, string fileSystemId, long length = 0)
        {
            return new PhysicalFileSystemEntry(
                path,
                kind,
                length,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                false,
                SourceMetadata.None,
                fileSystemId);
        }

        private void AddParents(string relativePath)
        {
            var parent = Path.GetDirectoryName(relativePath);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }

            var path = FullPath(parent);
            if (!_entries.ContainsKey(path))
            {
                AddDirectory(parent);
            }
        }

        private string FullPath(string relativePath)
        {
            return Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static bool IsDirectChild(string path, string parent)
        {
            return Path.GetDirectoryName(path)?.Equals(parent, PathComparison) is true;
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}

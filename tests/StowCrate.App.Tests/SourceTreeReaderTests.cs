using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.App.Tests;

public sealed class SourceTreeReaderTests
{
    [Fact]
    public async Task RealDirectoryLinkIsVisibleWithoutTraversingItsTarget()
    {
        var root = Directory.CreateTempSubdirectory("StowCrate-tree-");
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
            var target = Directory.CreateDirectory(Path.Combine(root.FullName, "target"));
            await File.WriteAllTextAsync(Path.Combine(target.FullName, "private.txt"), "not followed", TestContext.Current.CancellationToken);
            try { Directory.CreateSymbolicLink(Path.Combine(source.FullName, "link"), target.FullName); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Assert.Skip("当前测试环境不能创建目录符号链接。"); }
            var reader = new SourceTreeReader();
            var result = await reader.ReadAsync(new SourceId(Guid.NewGuid()), source.FullName, TestContext.Current.CancellationToken);
            var entry = Assert.Single(result.Snapshot!.Entries);
            Assert.Equal("link", entry.Path.Value); Assert.Equal(FileSystemEntryKind.Link, entry.Kind);
            Assert.Equal("not followed", await File.ReadAllTextAsync(Path.Combine(target.FullName, "private.txt"), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new SourceId(Guid.NewGuid()), Path.Combine(source.FullName, "link"), TestContext.Current.CancellationToken));
        }
        finally { root.Delete(true); }
    }
}

using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed class LocalPhysicalPathResolverTests
{
    [Fact]
    public async Task ParentAliasResolvesBeforeOverlapValidation()
    {
        var root = Directory.CreateTempSubdirectory("StowCrate-path-");
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root.FullName, "target"));
            var child = Directory.CreateDirectory(Path.Combine(target.FullName, "child"));
            var alias = Path.Combine(root.FullName, "alias");
            CreateDirectoryAlias(alias, target.FullName);
            var resolver = new LocalPhysicalPathResolver();
            var physical = await resolver.ResolveAsync(target.FullName, default);
            var throughAlias = await resolver.ResolveAsync(Path.Combine(alias, "child"), default);
            var direct = await resolver.ResolveAsync(child.FullName, default);
            Assert.Equal(direct.ComparisonKey, throughAlias.ComparisonKey);
            Assert.True(physical.Overlaps(throughAlias));
            var missing = await resolver.ResolveAsync(Path.Combine(alias, "future"), default);
            Assert.True(physical.Overlaps(missing));
            Assert.False(Directory.Exists(Path.Combine(target.FullName, "future")));
            await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync(alias, default));
        }
        finally { DeleteFixture(root); }
    }

    [Fact]
    public async Task DanglingParentDoesNotBecomeApparentlySafePath()
    {
        var root = Directory.CreateTempSubdirectory("StowCrate-path-");
        try
        {
            var alias = Path.Combine(root.FullName, "alias");
            CreateDirectoryAlias(alias, Path.Combine(root.FullName, "missing"));
            await Assert.ThrowsAsync<IOException>(() => new LocalPhysicalPathResolver().ResolveAsync(Path.Combine(alias, "child"), default));
        }
        finally { DeleteFixture(root); }
    }

    [Fact]
    public void LexicalOverlapIsRetainedAfterPhysicalResolution()
    {
        var source = new ResolvedPhysicalPath("/source", "/source");
        var output = new ResolvedPhysicalPath("/elsewhere/output", "/elsewhere/output", "/source/link/output");
        Assert.True(source.Overlaps(output));
        Assert.True(output.Overlaps(source));
    }

    private static void CreateDirectoryAlias(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        // Windows 使用无需符号链接权限的 Junction，确保本地测试也真实覆盖重解析点。
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{path}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
    }

    private static void DeleteFixture(DirectoryInfo root)
    {
        var alias = Path.Combine(root.FullName, "alias");
        // 先删除链接本身，不递归进入目标，也兼容指向不存在目录的 Junction。
        if (new DirectoryInfo(alias).LinkTarget is not null) Directory.Delete(alias);
        root.Delete(true);
    }
}

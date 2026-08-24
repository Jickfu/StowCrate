using System.Text;
using StowCrate.Core.Paths;

namespace StowCrate.Core.Tests.Paths;

public sealed class LogicalPathTests
{
    [Fact]
    public void PathNormalizesSeparatorsAndUnicodeToNfc()
    {
        var decomposedName = "é".Normalize(NormalizationForm.FormD);

        var path = new LogicalPath($"Code\\{decomposedName}\\File.cs");

        Assert.Equal("Code/é/File.cs", path.Value);
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("\\absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    public void PathRejectsAbsoluteOrEscapingValues(string value)
    {
        Assert.Throws<ArgumentException>(() => new LogicalPath(value));
    }

    [Fact]
    public void RelativeToUsesLogicalSeparatorsOnEveryPlatform()
    {
        var path = new LogicalPath("C/D/E/file.txt");

        var relative = path.RelativeTo(new LogicalPath("C/D"));

        Assert.Equal("E/file.txt", relative.Value);
    }

    [Fact]
    public void DefaultValueIsAValidRootPath()
    {
        LogicalPath logicalPath = default;
        RelativePath relativePath = default;

        Assert.True(logicalPath.IsRoot);
        Assert.True(relativePath.IsRoot);
        Assert.Equal(string.Empty, logicalPath.Value);
        Assert.Equal(string.Empty, relativePath.Value);
    }
}

using System.Xml.Linq;

namespace StowCrate.Architecture.Tests;

public sealed class ProjectDependencyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void SourceProjectsFollowTheAllowedDependencyGraph()
    {
        var expectedReferences = new Dictionary<string, string[]>
        {
            ["StowCrate.Core"] = [],
            ["StowCrate.Application"] = ["StowCrate.Core"],
            ["StowCrate.Infrastructure"] = ["StowCrate.Application", "StowCrate.Core"],
            ["StowCrate.Archiving"] = ["StowCrate.Application", "StowCrate.Core"],
            ["StowCrate.App"] = ["StowCrate.Application", "StowCrate.Archiving", "StowCrate.Infrastructure"],
        };

        foreach (var (projectName, expected) in expectedReferences)
        {
            var actual = ReadProjectReferences(ProjectPath(projectName));

            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData("StowCrate.Core")]
    [InlineData("StowCrate.Application")]
    public void InnerProjectsDoNotReferenceUiOrDatabasePackages(string projectName)
    {
        var packageReferences = ReadPackageReferences(ProjectPath(projectName));

        Assert.DoesNotContain(packageReferences, IsUiOrDatabasePackage);
    }

    [Fact]
    public void ViewModelsDoNotReferenceSqliteDirectly()
    {
        var viewModelsPath = Path.Combine(RepositoryRoot, "src", "StowCrate.App", "ViewModels");
        var viewModelSources = Directory.GetFiles(viewModelsPath, "*.cs", SearchOption.AllDirectories);

        foreach (var sourcePath in viewModelSources)
        {
            var source = File.ReadAllText(sourcePath);

            Assert.DoesNotContain("Microsoft.Data.Sqlite", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Data.SQLite", source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(@"..\StowCrate.Core\StowCrate.Core.csproj", "StowCrate.Core")]
    [InlineData("../StowCrate.Core/StowCrate.Core.csproj", "StowCrate.Core")]
    public void ProjectReferenceNamesAreParsedAcrossPlatforms(string reference, string expectedName)
    {
        Assert.Equal(expectedName, GetReferencedProjectName(reference));
    }

    private static bool IsUiOrDatabasePackage(string packageName)
    {
        return packageName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
            || packageName.Contains("SQLite", StringComparison.OrdinalIgnoreCase)
            || packageName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
    }

    private static string ProjectPath(string projectName)
    {
        return Path.Combine(RepositoryRoot, "src", projectName, $"{projectName}.csproj");
    }

    private static string[] ReadProjectReferences(string projectPath)
    {
        return ReadReferences(projectPath, "ProjectReference")
            .Select(GetReferencedProjectName)
            .ToArray();
    }

    private static string GetReferencedProjectName(string reference)
    {
        var normalizedReference = reference.Replace('\\', '/');

        return Path.GetFileNameWithoutExtension(normalizedReference)
            ?? throw new InvalidDataException($"无效的项目引用路径：{reference}");
    }

    private static string[] ReadPackageReferences(string projectPath)
    {
        return ReadReferences(projectPath, "PackageReference").ToArray();
    }

    private static IEnumerable<string> ReadReferences(string projectPath, string elementName)
    {
        var document = XDocument.Load(projectPath);

        return document.Descendants(elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StowCrate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到包含 StowCrate.slnx 的仓库根目录。");
    }
}

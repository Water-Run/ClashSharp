using System.Xml.Linq;

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the compile-time dependency direction between architectural projects.</summary>
public sealed class LayerDependencyDirectionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string SolutionRoot = Path.Combine(RepositoryRoot, "ClashSharp");

    /// <summary>Verifies the domain core has no project dependency.</summary>
    [Fact]
    public void CoreProject_HasNoProjectReferences()
    {
        Assert.Empty(ReadProjectReferences("ClashSharp.Core"));
    }

    /// <summary>Verifies application use cases depend only on the domain core.</summary>
    [Fact]
    public void ApplicationProject_DependsOnlyOnCore()
    {
        Assert.Equal(
            ["ClashSharp.Core"],
            ReadProjectReferences("ClashSharp.Application"));
    }

    /// <summary>Verifies infrastructure points inward and never references the WinUI presentation project.</summary>
    [Fact]
    public void InfrastructureProject_DependsOnlyOnApplicationAndCore()
    {
        Assert.Equal(
            ["ClashSharp.Application", "ClashSharp.Core"],
            ReadProjectReferences("ClashSharp.Infrastructure"));
    }

    /// <summary>Verifies the WinUI composition root references all inward layers.</summary>
    [Fact]
    public void WinUiProject_ReferencesRequiredInwardLayers()
    {
        IReadOnlySet<string> references = ReadProjectReferences("ClashSharp").ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ClashSharp.Core", references);
        Assert.Contains("ClashSharp.Application", references);
        Assert.Contains("ClashSharp.Infrastructure", references);
    }

    private static string[] ReadProjectReferences(string projectName)
    {
        string projectPath = Path.Combine(
            SolutionRoot,
            projectName,
            $"{projectName}.csproj");
        XDocument project = XDocument.Load(projectPath);

        return project
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .OfType<string>()
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Replace('\\', Path.DirectorySeparatorChar)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ClashSharp repository root.");
    }
}

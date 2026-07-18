using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClashSharp.Infrastructure;
using ClashSharp.Model;

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards repository policy and production assembly boundaries.</summary>
public sealed class RepositoryTopologyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>Verifies required repository policy artifacts are version controlled.</summary>
    [Fact]
    public void RepositoryPolicyArtifacts_ArePresent()
    {
        string[] paths =
        [
            ".gitattributes",
            "global.json",
            "Directory.Build.props",
            "rust-toolchain.toml",
            ".github/workflows/ci.yml",
            "CodingStyle.md",
            "docs/architecture/stabilization-ledger.md",
            "eng/dependency-audit-exceptions.json",
            "eng/tool-versions.json",
        ];

        Assert.All(paths, path => Assert.True(File.Exists(Path.Combine(RepositoryRoot, path)), path));
    }

    /// <summary>Verifies tests and the app reference the production Core and Infrastructure projects.</summary>
    [Fact]
    public void ProductionProjects_AreReferencedWithoutActiveConnectionSourceLink()
    {
        string testProjectPath = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp.Tests", "ClashSharp.Tests.csproj");
        string appProjectPath = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp", "ClashSharp.csproj");
        XDocument testProject = XDocument.Load(testProjectPath);
        XDocument appProject = XDocument.Load(appProjectPath);

        AssertProjectReference(testProject, "ClashSharp.Core");
        AssertProjectReference(testProject, "ClashSharp.Infrastructure");
        AssertProjectReference(appProject, "ClashSharp.Core");
        AssertProjectReference(appProject, "ClashSharp.Infrastructure");

        IEnumerable<string> compileIncludes = testProject.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>();
        Assert.DoesNotContain(compileIncludes, include => include.EndsWith("Model\\ActiveConnection.cs", StringComparison.Ordinal));
    }

    /// <summary>Verifies migrated types are loaded from production assemblies.</summary>
    [Fact]
    public void MigratedTypes_AreLoadedFromProductionAssemblies()
    {
        Assert.Equal("ClashSharp.Core", typeof(ActiveConnection).Assembly.GetName().Name);
        Assert.Equal("ClashSharp.Infrastructure", typeof(InfrastructureAssemblyMarker).Assembly.GetName().Name);
    }

    /// <summary>Verifies workflow actions are immutable and workflow permissions are read-only.</summary>
    [Fact]
    public void ContinuousIntegration_UsesImmutableActionsAndReadOnlyPermissions()
    {
        string workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        MatchCollection uses = Regex.Matches(workflow, @"uses:\s*[^@\s]+@(?<revision>[^\s#]+)");

        Assert.NotEmpty(uses);
        Assert.All(uses.Cast<Match>(), match => Assert.Matches("^[0-9a-f]{40}$", match.Groups["revision"].Value));
        Assert.Contains("permissions:\n  contents: read", workflow.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
    }

    /// <summary>Verifies RustSec exceptions are scoped, owned, justified, and time bounded.</summary>
    [Fact]
    public void DependencyAuditExceptions_AreDocumentedAndUnexpired()
    {
        string path = Path.Combine(RepositoryRoot, "eng", "dependency-audit-exceptions.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement exceptions = document.RootElement.GetProperty("rustsecExceptions");
        string[] advisoryIds = exceptions.EnumerateArray()
            .Select(item => item.GetProperty("advisoryId").GetString())
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["RUSTSEC-2026-0194", "RUSTSEC-2026-0195"], advisoryIds);
        foreach (JsonElement exception in exceptions.EnumerateArray())
        {
            Assert.Equal("quick-xml", exception.GetProperty("package").GetString());
            Assert.Equal("0.39.4", exception.GetProperty("version").GetString());
            Assert.Equal("ClashSharp/Installer/Cargo.lock", exception.GetProperty("lockFile").GetString());
            Assert.False(string.IsNullOrWhiteSpace(exception.GetProperty("introducedBy").GetString()));
            Assert.Equal("x86_64-pc-windows-msvc", exception.GetProperty("releaseTarget").GetString());
            Assert.Equal("Release", exception.GetProperty("owner").GetString());
            Assert.Equal("Phase 11", exception.GetProperty("reviewPhase").GetString());
            Assert.False(string.IsNullOrWhiteSpace(exception.GetProperty("rationale").GetString()));
            DateOnly expiresOn = DateOnly.Parse(exception.GetProperty("expiresOn").GetString()!, CultureInfo.InvariantCulture);
            Assert.True(expiresOn >= DateOnly.FromDateTime(DateTime.UtcNow), $"RustSec exception expired on {expiresOn:O}.");
        }

        string workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        string[] workflowAdvisoryIds = Regex.Matches(workflow, @"--ignore\s+(?<advisory>RUSTSEC-\d{4}-\d{4})")
            .Select(match => match.Groups["advisory"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(advisoryIds, workflowAdvisoryIds);

        string toolVersionsPath = Path.Combine(RepositoryRoot, "eng", "tool-versions.json");
        using JsonDocument toolVersions = JsonDocument.Parse(File.ReadAllText(toolVersionsPath));
        string cargoAuditVersion = toolVersions.RootElement.GetProperty("cargoAudit").GetString()!;
        Assert.Contains($"cargo install cargo-audit --version {cargoAuditVersion} --locked", workflow, StringComparison.Ordinal);
    }

    private static void AssertProjectReference(XDocument project, string projectName)
    {
        IEnumerable<string> includes = project.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>();
        Assert.Contains(includes, include => include.Contains(projectName, StringComparison.Ordinal));
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

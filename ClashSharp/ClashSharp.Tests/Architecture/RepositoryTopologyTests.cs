using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Startup;
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

    /// <summary>Verifies C# sources rely on source control and project-level nullability policy.</summary>
    [Fact]
    public void CSharpSources_DoNotUseVolatileBannersOrRedundantNullableDirectives()
    {
        string sourceRoot = Path.Combine(RepositoryRoot, "ClashSharp");
        string[] offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relativePath = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
                return !relativePath.Contains("/bin/", StringComparison.Ordinal)
                    && !relativePath.Contains("/obj/", StringComparison.Ordinal)
                    && !relativePath.StartsWith("bin/", StringComparison.Ordinal)
                    && !relativePath.StartsWith("obj/", StringComparison.Ordinal);
            })
            .Where(path =>
            {
                string source = File.ReadAllText(path);
                return Regex.IsMatch(source, @"@(?:author|file|date):", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(source, @"(?m)^#nullable enable\s*$");
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Volatile source banners or redundant nullable directives found:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
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
        AssertProjectReference(testProject, "ClashSharp.Application");
        AssertProjectReference(appProject, "ClashSharp.Core");
        AssertProjectReference(appProject, "ClashSharp.Infrastructure");
        AssertProjectReference(appProject, "ClashSharp.Application");

        IEnumerable<string> compileIncludes = testProject.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>();
        Assert.DoesNotContain(compileIncludes, include => include.EndsWith("Model\\ActiveConnection.cs", StringComparison.Ordinal));
    }

    /// <summary>Verifies executable integration probes cannot be published or packed as products.</summary>
    [Fact]
    public void IntegrationProbeProjects_AreExplicitlyTestOnlyAndNonPublishable()
    {
        string[] probeNames =
        [
            "ClashSharp.ProcessProbe",
            "ClashSharp.SettingsProbe",
            "ClashSharp.StartupProbe",
            "ClashSharp.TriggerProbe",
        ];

        foreach (string probeName in probeNames)
        {
            string path = Path.Combine(
                RepositoryRoot,
                "ClashSharp",
                probeName,
                $"{probeName}.csproj");
            XDocument project = XDocument.Load(path);
            Dictionary<string, string> properties = project
                .Descendants("PropertyGroup")
                .Elements()
                .GroupBy(static element => element.Name.LocalName, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Last().Value.Trim(),
                    StringComparer.Ordinal);

            Assert.Equal("true", properties["IsTestProject"]);
            Assert.Equal("false", properties["IsPublishable"]);
            Assert.Equal("false", properties["IsPackable"]);
        }
    }

    /// <summary>Verifies migrated types are loaded from production assemblies.</summary>
    [Fact]
    public void MigratedTypes_AreLoadedFromProductionAssemblies()
    {
        Assert.Equal("ClashSharp.Core", typeof(ActiveConnection).Assembly.GetName().Name);
        Assert.Equal("ClashSharp.Core", typeof(ClashSharpMode).Assembly.GetName().Name);
        Assert.Equal("ClashSharp.Core", typeof(NetworkTakeoverResult).Assembly.GetName().Name);
        Assert.Equal("ClashSharp.Infrastructure", typeof(InfrastructureAssemblyMarker).Assembly.GetName().Name);
        Assert.Equal("ClashSharp.Application", typeof(ApplicationBootstrapper).Assembly.GetName().Name);
    }

    /// <summary>Verifies WinUI launch delegates startup ownership to the production bootstrap pipeline.</summary>
    [Fact]
    public void WinUiLaunch_UsesOwnershipFirstCompositionWithoutLegacyDuplicateProcessFlow()
    {
        string appPath = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp", "App.xaml.cs");
        string mainWindowPath = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp", "MainWindow.xaml.cs");
        string legacyServicePath = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp", "Service", "SingleInstanceService.cs");
        string app = File.ReadAllText(appPath);
        string mainWindow = File.ReadAllText(mainWindowPath);

        Assert.Contains("ApplicationBootstrapper", app, StringComparison.Ordinal);
        Assert.Contains("WindowsPrimaryInstanceBootstrap", app, StringComparison.Ordinal);
        Assert.Contains("private MainWindow CreateMainWindow()", app, StringComparison.Ordinal);
        Assert.Contains("MainWindowComposition.CreateStartupShell(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(ApplyStartupProxyRecovery", app, StringComparison.Ordinal);
        Assert.DoesNotContain("TriggerService.Instance.Start", app, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsAuditLogService.Instance.Start", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionSamplingService.Instance.StartIfEnabled", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SingleInstanceService", mainWindow, StringComparison.Ordinal);
        Assert.False(File.Exists(legacyServicePath));
    }

    /// <summary>Verifies runtime lifecycle ownership is direct and the legacy adapter cannot return.</summary>
    [Fact]
    public void AppHost_RegistersSupervisedRuntimeParticipantsDirectly()
    {
        string hostPath = Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "ClashSharp",
            "AppHost",
            "ClashSharpAppHostFactory.cs");
        string compatibilityPath = Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "ClashSharp",
            "AppHost",
            "Compatibility",
            "LegacyRuntimeParticipants.cs");
        string startupPath = Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "ClashSharp",
            "AppHost",
            "Startup",
            "ConnectionSamplingStartupStep.cs");
        string host = File.ReadAllText(hostPath);
        string startup = File.ReadAllText(startupPath);

        Assert.Contains("GetRequiredService<ConnectionSamplingService>()", host, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<TriggerScheduler>()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyConnectionSamplingRuntimeParticipant", host, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyTriggerRuntimeParticipant", host, StringComparison.Ordinal);
        Assert.False(File.Exists(compatibilityPath));
        Assert.Contains("ConnectionSamplingService sampling", startup, StringComparison.Ordinal);
    }

    /// <summary>Prevents presentation service-locator debt from returning after the AppHost cutover.</summary>
    [Fact]
    public void PresentationServiceLocatorDebt_DoesNotIncrease()
    {
        string presentationRoot = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp");
        Dictionary<string, int> actualOccurrencesByFile = Directory
            .EnumerateFiles(presentationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(presentationRoot, path).Replace('\\', '/');
                return !relative.StartsWith("obj/", StringComparison.Ordinal)
                    && !relative.StartsWith("bin/", StringComparison.Ordinal)
                    && (relative.StartsWith("Presentation/", StringComparison.Ordinal)
                        || relative.StartsWith("View/", StringComparison.Ordinal)
                        || relative.StartsWith("ViewModel/", StringComparison.Ordinal));
            })
            .Select(path => new
            {
                RelativePath = Path.GetRelativePath(presentationRoot, path).Replace('\\', '/'),
                Count = Regex.Count(
                    File.ReadAllText(path),
                    @"\b[A-Za-z_][A-Za-z0-9_]*Service\.Instance\b"),
            })
            .Where(static item => item.Count > 0)
            .ToDictionary(static item => item.RelativePath, static item => item.Count, StringComparer.Ordinal);

        Assert.Empty(actualOccurrencesByFile);
    }

    /// <summary>Verifies startup steps receive dependencies from the host composition root.</summary>
    [Fact]
    public void StartupSteps_DoNotResolveProcessWideServiceInstances()
    {
        string startupRoot = Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "ClashSharp",
            "AppHost",
            "Startup");
        string[] offenders = Directory
            .EnumerateFiles(startupRoot, "*Step.cs", SearchOption.TopDirectoryOnly)
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                @"\b[A-Za-z_][A-Za-z0-9_]*Service\.Instance\b"))
            .Select(static path => Path.GetFileName(path) ?? path)
            .OrderBy(static fileName => fileName, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Startup steps must use constructor-injected dependencies: " + string.Join(", ", offenders));
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

        foreach (JsonElement exception in exceptions.EnumerateArray())
        {
            string package = exception.GetProperty("package").GetString()!;
            string version = exception.GetProperty("version").GetString()!;
            string lockFile = exception.GetProperty("lockFile").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(package));
            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.False(string.IsNullOrWhiteSpace(lockFile));
            Assert.False(string.IsNullOrWhiteSpace(exception.GetProperty("introducedBy").GetString()));
            Assert.Equal("x86_64-pc-windows-msvc", exception.GetProperty("releaseTarget").GetString());
            Assert.Equal("Release", exception.GetProperty("owner").GetString());
            Assert.Equal("Phase 11", exception.GetProperty("reviewPhase").GetString());
            Assert.False(string.IsNullOrWhiteSpace(exception.GetProperty("rationale").GetString()));
            DateOnly expiresOn = DateOnly.Parse(exception.GetProperty("expiresOn").GetString()!, CultureInfo.InvariantCulture);
            Assert.True(expiresOn >= DateOnly.FromDateTime(DateTime.UtcNow), $"RustSec exception expired on {expiresOn:O}.");

            string lockText = File.ReadAllText(Path.Combine(RepositoryRoot, lockFile));
            Assert.Contains($"name = \"{package}\"", lockText, StringComparison.Ordinal);
            Assert.Contains($"version = \"{version}\"", lockText, StringComparison.Ordinal);
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

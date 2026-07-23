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
        Assert.DoesNotContain("new MainWindow", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(ApplyStartupProxyRecovery", app, StringComparison.Ordinal);
        Assert.DoesNotContain("TriggerService.Instance.Start", app, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsAuditLogService.Instance.Start", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionSamplingService.Instance.StartIfEnabled", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SingleInstanceService", mainWindow, StringComparison.Ordinal);
        Assert.False(File.Exists(legacyServicePath));
    }

    /// <summary>Verifies sampling lifecycle ownership is direct and the legacy adapter cannot return.</summary>
    [Fact]
    public void AppHost_RegistersSamplingAsItsDirectRuntimeParticipant()
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
        string compatibility = File.ReadAllText(compatibilityPath);
        string startup = File.ReadAllText(startupPath);

        Assert.Contains("GetRequiredService<ConnectionSamplingService>()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyConnectionSamplingRuntimeParticipant", host, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyConnectionSamplingRuntimeParticipant", compatibility, StringComparison.Ordinal);
        Assert.Contains("ConnectionSamplingService sampling", startup, StringComparison.Ordinal);
    }

    /// <summary>Freezes presentation service-locator debt by file so migration can only reduce it.</summary>
    [Fact]
    public void PresentationServiceLocatorDebt_DoesNotIncrease()
    {
        Dictionary<string, int> maximumOccurrencesByFile = new(StringComparer.Ordinal)
        {
            ["View/About.xaml.cs"] = 8,
            ["View/Connections.xaml.cs"] = 4,
            ["View/Links.xaml.cs"] = 9,
            ["View/Logs.xaml.cs"] = 14,
            ["View/MasterControl.xaml.cs"] = 20,
            ["View/Profiles.xaml.cs"] = 5,
            ["View/Proxies.xaml.cs"] = 5,
            ["View/Rules.xaml.cs"] = 2,
            ["View/Settings.xaml.cs"] = 60,
            ["View/StartupConflictDialogPresenter.cs"] = 8,
            ["View/Statistics.xaml.cs"] = 3,
            ["View/Triggers.xaml.cs"] = 40,
            ["ViewModel/AsyncRelayCommand.cs"] = 1,
            ["ViewModel/MainWindowViewModel.cs"] = 1,
            ["ViewModel/ManagementPageViewModels.cs"] = 1,
            ["ViewModel/MasterControlAdapters.cs"] = 11,
            ["ViewModel/MasterControlViewModel.cs"] = 5,
            ["ViewModel/ProxiesViewModel.cs"] = 1,
            ["ViewModel/SettingsAdapters.cs"] = 3,
            ["ViewModel/SettingsViewModel.cs"] = 7,
        };
        string presentationRoot = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp");
        Dictionary<string, int> actualOccurrencesByFile = Directory
            .EnumerateFiles(presentationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(presentationRoot, path).Replace('\\', '/');
                return !relative.StartsWith("obj/", StringComparison.Ordinal)
                    && !relative.StartsWith("bin/", StringComparison.Ordinal)
                    && (relative.StartsWith("View/", StringComparison.Ordinal)
                        || relative.StartsWith("ViewModel/", StringComparison.Ordinal));
            })
            .Select(path => new
            {
                RelativePath = Path.GetRelativePath(presentationRoot, path).Replace('\\', '/'),
                Count = Regex.Count(File.ReadAllText(path), @"\.Instance\b"),
            })
            .Where(static item => item.Count > 0)
            .ToDictionary(static item => item.RelativePath, static item => item.Count, StringComparer.Ordinal);

        Assert.True(
            actualOccurrencesByFile.Values.Sum() <= 208,
            "Presentation service-locator debt exceeded the 208-reference Phase 03 baseline.");
        Assert.All(actualOccurrencesByFile, occurrence =>
        {
            Assert.True(
                maximumOccurrencesByFile.TryGetValue(occurrence.Key, out int maximum),
                $"New presentation service-locator file: {occurrence.Key}");
            Assert.True(
                occurrence.Value <= maximum,
                $"Presentation service-locator debt increased in {occurrence.Key}: {occurrence.Value} > {maximum}.");
        });
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

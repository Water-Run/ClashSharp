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
            ".github/workflows/ci.yml",
            "CodingStyle.md",
            "docs/architecture/stabilization-ledger.md",
        ];

        Assert.All(paths, path => Assert.True(File.Exists(Path.Combine(RepositoryRoot, path)), path));
        string ignoreRules = File.ReadAllText(Path.Combine(RepositoryRoot, ".gitignore"));
        Assert.Contains(
            "!ClashSharp/ClashSharp.Installer.Windows/Packages/**",
            ignoreRules,
            StringComparison.Ordinal);
    }

    /// <summary>Verifies every production assembly emits XML documentation and rejects missing public contracts.</summary>
    [Fact]
    public void ProductionProjects_EnforceCompletePublicXmlDocumentation()
    {
        string[] projectNames =
        [
            "ClashSharp.Core",
            "ClashSharp.Application",
            "ClashSharp.Infrastructure",
            "ClashSharp",
            "ClashSharp.MihomoService",
            "ClashSharp.RecoveryWatchdog",
            "ClashSharp.Installer.Core",
            "ClashSharp.Installer.Presentation",
            "ClashSharp.Installer.Windows",
            "ClashSharp.Installer",
        ];

        foreach (string projectName in projectNames)
        {
            string projectPath = Path.Combine(
                RepositoryRoot,
                "ClashSharp",
                projectName,
                $"{projectName}.csproj");
            XDocument project = XDocument.Load(projectPath);
            Dictionary<string, string> properties = project
                .Descendants("PropertyGroup")
                .Elements()
                .GroupBy(static element => element.Name.LocalName, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Last().Value.Trim(),
                    StringComparer.Ordinal);

            Assert.Equal("true", properties["GenerateDocumentationFile"]);
            Assert.Contains("CS1591", properties["WarningsAsErrors"], StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies Windows system-library imports cannot resolve through writable search locations.</summary>
    [Fact]
    public void ProductionPInvokeDeclarations_AreRestrictedToSystem32()
    {
        string[] productionProjectNames =
        [
            "ClashSharp.Core",
            "ClashSharp.Application",
            "ClashSharp.Infrastructure",
            "ClashSharp",
            "ClashSharp.MihomoService",
            "ClashSharp.RecoveryWatchdog",
            "ClashSharp.Installer.Core",
            "ClashSharp.Installer.Presentation",
            "ClashSharp.Installer.Windows",
            "ClashSharp.Installer",
        ];

        string[] offenders = productionProjectNames
            .SelectMany(projectName => Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "ClashSharp", projectName),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path =>
            {
                string relativePath = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');
                return !relativePath.Contains("/bin/", StringComparison.Ordinal)
                    && !relativePath.Contains("/obj/", StringComparison.Ordinal);
            })
            .SelectMany(FindUnrestrictedPInvokeDeclarations)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Unrestricted production P/Invoke declarations found:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>Verifies every maintained PowerShell function has adjacent comment-based help.</summary>
    [Fact]
    public void MaintainedPowerShellFunctions_HaveCompleteCommentBasedHelp()
    {
        string[] sourceRoots =
        [
            Path.Combine(RepositoryRoot, "ClashSharp", "Installer"),
            Path.Combine(RepositoryRoot, "ClashSharp", "SandboxTest"),
            Path.Combine(RepositoryRoot, "Tools"),
            Path.Combine(RepositoryRoot, "eng"),
        ];
        var functionPattern = new Regex(
            @"(?m)^function\s+(?<name>[A-Za-z][A-Za-z0-9-]*)\s*\{",
            RegexOptions.CultureInvariant);
        var parameterPattern = new Regex(
            @"(?m)^\s*\.PARAMETER\s+(?<name>[A-Za-z][A-Za-z0-9]*)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var offenders = new List<string>();

        foreach (string path in sourceRoots
                     .Where(Directory.Exists)
                     .SelectMany(root => Directory.EnumerateFiles(
                         root,
                         "*.ps*1",
                         SearchOption.AllDirectories))
                     .Where(path => !path.Contains(
                         $"{Path.DirectorySeparatorChar}.sandbox{Path.DirectorySeparatorChar}",
                         StringComparison.OrdinalIgnoreCase)))
        {
            string source = File.ReadAllText(path);
            foreach (Match function in functionPattern.Matches(source))
            {
                string body = ReadPowerShellFunctionBody(source, function.Index);
                string help = ReadPowerShellFunctionHelp(source, function.Index, body);
                string functionName = function.Groups["name"].Value;
                if (!help.Contains(".SYNOPSIS", StringComparison.OrdinalIgnoreCase)
                    || !help.Contains(".DESCRIPTION", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(RelativeFunction(path, functionName));
                    continue;
                }

                string parameterBlock = ReadPowerShellParameterBlock(body);
                string[] parameters = Regex.Matches(
                        parameterBlock,
                        @"\$(?<name>[A-Za-z][A-Za-z0-9]*)",
                        RegexOptions.CultureInvariant)
                    .Select(match => match.Groups["name"].Value)
                    .Where(name => name is not ("true" or "false" or "null"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                HashSet<string> documentedParameters = parameterPattern
                    .Matches(help)
                    .Select(match => match.Groups["name"].Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (parameters.Any(parameter => !documentedParameters.Contains(parameter)))
                {
                    offenders.Add(RelativeFunction(path, functionName));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"PowerShell functions missing complete help:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
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

    /// <summary>Verifies the repository exposes only the main app and one Installer as user products.</summary>
    [Fact]
    public void ExecutableTopology_HasOneMainApplicationAndOneInstallerProduct()
    {
        string projectsRoot = Path.Combine(RepositoryRoot, "ClashSharp");
        string[] installerInternalComponents =
        [
            "ClashSharp.MihomoService",
            "ClashSharp.RecoveryWatchdog",
        ];
        (string Path, XDocument Project)[] executableProjects = Directory
            .EnumerateFiles(projectsRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Project: XDocument.Load(path)))
            .Where(item => item.Project
                .Descendants("OutputType")
                .Any(element => element.Value.Trim() is "Exe" or "WinExe"))
            .ToArray();

        string[] userProducts = executableProjects
            .Where(item => !HasProjectProperty(item.Project, "IsTestProject", "true"))
            .Where(item => !installerInternalComponents.Contains(
                Path.GetFileNameWithoutExtension(item.Path),
                StringComparer.Ordinal))
            .Select(item => Path.GetFileNameWithoutExtension(item.Path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ClashSharp", "ClashSharp.Installer"], userProducts);
        Assert.All(
            executableProjects.Where(item => installerInternalComponents.Contains(
                Path.GetFileNameWithoutExtension(item.Path),
                StringComparer.Ordinal)),
            item =>
            {
                XElement[] publishability = item.Project
                    .Descendants("IsPublishable")
                    .ToArray();
                Assert.Contains(
                    publishability,
                    element => element.Value.Trim() == "false"
                        && ((string?)element.Attribute("Condition"))?.Contains(
                            "!='true'",
                            StringComparison.Ordinal) == true
                        && ((string?)element.Attribute("Condition"))?.Contains(
                            "ClashSharpFormalInstallerComponent",
                            StringComparison.Ordinal) == true);
                Assert.Contains(
                    publishability,
                    element => element.Value.Trim() == "true"
                        && ((string?)element.Attribute("Condition"))?.Contains(
                            "=='true'",
                            StringComparison.Ordinal) == true
                        && ((string?)element.Attribute("Condition"))?.Contains(
                            "ClashSharpFormalInstallerComponent",
                            StringComparison.Ordinal) == true);
                Assert.True(HasProjectProperty(item.Project, "IsPackable", "false"));
            });
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
        string normalizedWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        MatchCollection uses = Regex.Matches(workflow, @"uses:\s*[^@\s]+@(?<revision>[^\s#]+)");

        Assert.NotEmpty(uses);
        Assert.All(uses.Cast<Match>(), match => Assert.Matches("^[0-9a-f]{40}$", match.Groups["revision"].Value));
        Assert.Contains("permissions:\n  contents: read", normalizedWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("\n    env:\n      Platform: x64", normalizedWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "ClashSharp.Installer.Tests.csproj\n          -c Release -p:Platform=AnyCPU --no-build",
            normalizedWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClashSharp.Installer.Presentation.Tests.csproj\n          -c Release -p:Platform=AnyCPU --no-build",
            normalizedWorkflow,
            StringComparison.Ordinal);
    }

    /// <summary>Ensures CI exercises the Sandbox report gate in both supported PowerShell editions.</summary>
    [Fact]
    public void ContinuousIntegration_EnforcesBoundSandboxEvidence()
    {
        string workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        string sandboxRoot = Path.Combine(RepositoryRoot, "ClashSharp", "SandboxTest");
        string host = File.ReadAllText(Path.Combine(sandboxRoot, "Run-SandboxTest.ps1"));
        string guest = File.ReadAllText(Path.Combine(sandboxRoot, "scripts", "Run-InSandbox.ps1"));

        Assert.Contains("./ClashSharp/SandboxTest/Test-SandboxReportContract.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify Sandbox report contract (Windows PowerShell 5.1)", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify Sandbox report contract (PowerShell 7)", workflow, StringComparison.Ordinal);
        Assert.Contains("SandboxReportContract\\Assert-SandboxScenarioReport", host, StringComparison.Ordinal);
        Assert.Contains("RunId = $runId", host, StringComparison.Ordinal);
        Assert.Contains("-ExpectedRunId $run.RunId", host, StringComparison.Ordinal);
        Assert.Contains("runId = [string]$Plan.runId", guest, StringComparison.Ordinal);
        Assert.DoesNotContain("-Status \"skipped\"", guest, StringComparison.Ordinal);
    }

    /// <summary>Prevents the retired native Installer toolchain and UI from returning.</summary>
    [Fact]
    public void Repository_ContainsOnlyTheCSharpInstallerImplementation()
    {
        string[] forbiddenFiles = Directory
            .EnumerateFiles(RepositoryRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Where(relativePath =>
                !relativePath.StartsWith(".git/", StringComparison.Ordinal)
                && !relativePath.Contains("/bin/", StringComparison.Ordinal)
                && !relativePath.Contains("/obj/", StringComparison.Ordinal)
                && !relativePath.Contains("/artifacts/", StringComparison.Ordinal)
                && !relativePath.Contains("/target/", StringComparison.Ordinal)
                && !relativePath.Contains("/.sandbox/", StringComparison.Ordinal))
            .Where(relativePath =>
                relativePath.EndsWith(".rs", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith(".slint", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relativePath).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relativePath).Equals("Cargo.lock", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(relativePath).StartsWith("rust-toolchain", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            forbiddenFiles.Length == 0,
            $"Retired Installer source/toolchain files found:{Environment.NewLine}{string.Join(Environment.NewLine, forbiddenFiles)}");

        string workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        Assert.DoesNotContain("cargo", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rust", workflow, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindUnrestrictedPInvokeDeclarations(string path)
    {
        string[] lines = File.ReadAllLines(path);
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (!Regex.IsMatch(
                    lines[lineIndex],
                    @"\[(?:System\.Runtime\.InteropServices\.)?(?:DllImport|LibraryImport)\s*\("))
            {
                continue;
            }

            bool isSystem32Restricted = false;
            int inspectionEnd = Math.Min(lines.Length - 1, lineIndex + 16);
            for (int inspectionIndex = lineIndex + 1; inspectionIndex <= inspectionEnd; inspectionIndex++)
            {
                if (lines[inspectionIndex].Contains(
                        "DefaultDllImportSearchPaths(DllImportSearchPath.System32)",
                        StringComparison.Ordinal))
                {
                    isSystem32Restricted = true;
                }

                if (Regex.IsMatch(lines[inspectionIndex], @"\b(?:extern|partial)\b"))
                {
                    break;
                }
            }

            if (!isSystem32Restricted)
            {
                string relativePath = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');
                yield return $"{relativePath}:{lineIndex + 1}";
            }
        }
    }

    private static bool HasProjectProperty(
        XDocument project,
        string propertyName,
        string expectedValue) =>
        project
            .Descendants(propertyName)
            .Any(element => string.Equals(
                element.Value.Trim(),
                expectedValue,
                StringComparison.OrdinalIgnoreCase));

    private static string ReadPowerShellFunctionBody(
        string source,
        int functionStart)
    {
        Match nextFunction = Regex.Match(
            source[(functionStart + 1)..],
            @"(?m)^function\s+",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        int functionEnd = nextFunction.Success
            ? functionStart + 1 + nextFunction.Index
            : source.Length;
        return source[functionStart..functionEnd];
    }

    private static string ReadPowerShellFunctionHelp(
        string source,
        int functionStart,
        string functionBody)
    {
        string preceding = source[..functionStart].TrimEnd();
        int precedingEnd = preceding.LastIndexOf("#>", StringComparison.Ordinal);
        int precedingStart = preceding.LastIndexOf("<#", StringComparison.Ordinal);
        if (precedingStart >= 0
            && precedingEnd == preceding.Length - 2
            && precedingStart < precedingEnd)
        {
            return preceding[precedingStart..(precedingEnd + 2)];
        }

        int openingBrace = functionBody.IndexOf('{');
        int internalStart = functionBody.IndexOf("<#", StringComparison.Ordinal);
        int internalEnd = functionBody.IndexOf("#>", StringComparison.Ordinal);
        return openingBrace >= 0
            && internalStart > openingBrace
            && internalEnd > internalStart
            && string.IsNullOrWhiteSpace(
                functionBody[(openingBrace + 1)..internalStart])
                ? functionBody[internalStart..(internalEnd + 2)]
                : string.Empty;
    }

    private static string ReadPowerShellParameterBlock(string functionBody)
    {
        Match parameterStart = Regex.Match(
            functionBody,
            @"(?m)^\s*param\s*\(",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!parameterStart.Success)
        {
            return string.Empty;
        }

        int openingParenthesis = functionBody.IndexOf(
            '(',
            parameterStart.Index);
        int depth = 0;
        for (int index = openingParenthesis; index < functionBody.Length; index++)
        {
            depth += functionBody[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (depth == 0)
            {
                return functionBody[(openingParenthesis + 1)..index];
            }
        }

        return string.Empty;
    }

    private static string RelativeFunction(string path, string functionName) =>
        $"{Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/')}::{functionName}";

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

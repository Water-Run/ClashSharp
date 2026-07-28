using System.Xml.Linq;

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards source placement and UI-framework boundaries in the WinUI presentation project.</summary>
public sealed class MvvmStructureTests
{
    private const string XamlLanguageNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ApplicationRoot = Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp");

    /// <summary>Verifies the View directory contains only code-behind paired with a XAML view.</summary>
    [Fact]
    public void ViewDirectory_ContainsOnlyPairedXamlCodeBehind()
    {
        string viewRoot = Path.Combine(ApplicationRoot, "View");
        string[] sourcePaths = EnumerateProductionFiles(viewRoot, "*.cs").ToArray();

        Assert.NotEmpty(sourcePaths);
        foreach (string sourcePath in sourcePaths)
        {
            string relativePath = Path.GetRelativePath(ApplicationRoot, sourcePath);
            Assert.True(
                sourcePath.EndsWith(".xaml.cs", StringComparison.Ordinal),
                $"{relativePath} is not XAML code-behind and must live outside View.");

            string xamlPath = sourcePath[..^".cs".Length];
            Assert.True(
                File.Exists(xamlPath),
                $"{relativePath} has no same-name XAML view.");
        }
    }

    /// <summary>Verifies presentation-layer folders and declared namespaces stay aligned.</summary>
    [Fact]
    public void PresentationDirectories_MatchDeclaredNamespaces()
    {
        string[] governedDirectories = ["Presentation", "View", "ViewModel", "Components"];

        foreach (string governedDirectory in governedDirectories)
        {
            string directoryRoot = Path.Combine(ApplicationRoot, governedDirectory);
            foreach (string sourcePath in EnumerateProductionFiles(directoryRoot, "*.cs"))
            {
                string relativeDirectory = Path.GetRelativePath(
                    ApplicationRoot,
                    Path.GetDirectoryName(sourcePath)!);
                string expectedNamespace = "ClashSharp." + relativeDirectory
                    .Replace(Path.DirectorySeparatorChar, '.')
                    .Replace(Path.AltDirectorySeparatorChar, '.');
                IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
                string[] declaredNamespaces = ReadDeclaredNamespaces(tokens).ToArray();

                string actualNamespace = Assert.Single(declaredNamespaces);
                Assert.True(
                    string.Equals(expectedNamespace, actualNamespace, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(ApplicationRoot, sourcePath)} declares {actualNamespace}; "
                    + $"expected {expectedNamespace} from its directory.");
            }
        }
    }

    /// <summary>Verifies models and view models do not depend on WinUI or legacy Windows UI namespaces.</summary>
    [Fact]
    public void ModelsAndViewModels_DoNotReferenceUiFrameworkNamespaces()
    {
        string[] governedDirectories = ["Model", "ViewModel"];

        foreach (string governedDirectory in governedDirectories)
        {
            string directoryRoot = Path.Combine(ApplicationRoot, governedDirectory);
            foreach (string sourcePath in EnumerateProductionFiles(directoryRoot, "*.cs"))
            {
                IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
                string? uiNamespace = FindUiFrameworkNamespace(tokens);

                Assert.True(
                    uiNamespace is null,
                    $"{Path.GetRelativePath(ApplicationRoot, sourcePath)} references {uiNamespace}; "
                    + "models and view models must expose platform-neutral state and commands.");
            }
        }
    }

    /// <summary>Verifies domain and data models do not resolve presentation services or global instances.</summary>
    [Fact]
    public void Models_DoNotReferenceServicesOrGlobalInstances()
    {
        string modelRoot = Path.Combine(ApplicationRoot, "Model");

        foreach (string sourcePath in EnumerateProductionFiles(modelRoot, "*.cs"))
        {
            IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
            bool referencesServiceNamespace = false;
            for (int index = 0; index < tokens.Count - 2; index++)
            {
                if (string.Equals(tokens[index], "ClashSharp", StringComparison.Ordinal)
                    && string.Equals(tokens[index + 1], ".", StringComparison.Ordinal)
                    && string.Equals(tokens[index + 2], "Service", StringComparison.Ordinal))
                {
                    referencesServiceNamespace = true;
                    break;
                }
            }

            string[] singletonOwners = ReadInstanceMemberOwners(tokens).ToArray();
            Assert.True(
                !referencesServiceNamespace && singletonOwners.Length == 0,
                $"{Path.GetRelativePath(ApplicationRoot, sourcePath)} crosses the model boundary: "
                + (referencesServiceNamespace ? "references ClashSharp.Service; " : string.Empty)
                + (singletonOwners.Length > 0
                    ? "resolves " + string.Join(", ", singletonOwners) + ".Instance."
                    : string.Empty));
        }
    }

    /// <summary>Verifies service-neutral boundary snapshots remain one matching model type per file.</summary>
    [Fact]
    public void BoundaryModelFiles_HaveOneMatchingPrimaryType()
    {
        string modelRoot = Path.Combine(ApplicationRoot, "Model");
        string[] modelTypeNames =
        [
            "LogStorageSummary",
            "StartupConflictIssue",
            "StartupConflictKind",
            "StartupConflictProcess",
            "StartupConflictRepairResult",
            "StartupRestoreFallbackStatus",
            "TrafficStatisticsSummary",
            "TrayStatusSnapshot",
        ];

        foreach (string modelTypeName in modelTypeNames)
        {
            string sourcePath = Path.Combine(modelRoot, modelTypeName + ".cs");
            Assert.True(File.Exists(sourcePath), $"Model/{modelTypeName}.cs is missing.");

            IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
            string actualTypeName = Assert.Single(ReadDeclaredTopLevelTypeNames(tokens));
            Assert.Equal(modelTypeName, actualTypeName);
        }
    }

    /// <summary>Verifies concrete adapters and their platform dependencies remain outside ViewModel.</summary>
    [Fact]
    public void ViewModels_DoNotContainAdaptersOrPlatformDependencies()
    {
        string viewModelRoot = Path.Combine(ApplicationRoot, "ViewModel");

        string[] misplacedAdapters = EnumerateProductionFiles(viewModelRoot, "*Adapters.cs")
            .Select(path => Path.GetRelativePath(ApplicationRoot, path))
            .ToArray();
        Assert.True(
            misplacedAdapters.Length == 0,
            "ViewModel contains concrete adapters: " + string.Join(", ", misplacedAdapters));

        foreach (string sourcePath in EnumerateProductionFiles(viewModelRoot, "*.cs"))
        {
            string source = File.ReadAllText(sourcePath);
            string relativePath = Path.GetRelativePath(ApplicationRoot, sourcePath);
            Assert.DoesNotContain("Windows.System", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.Data.Sqlite", source, StringComparison.Ordinal);
            Assert.DoesNotContain("namespace ClashSharp.Presentation.Adapters", source, StringComparison.Ordinal);
            Assert.False(
                source.Contains("using Windows.System;", StringComparison.Ordinal)
                || source.Contains("using Microsoft.Data.Sqlite;", StringComparison.Ordinal),
                $"{relativePath} depends on an adapter-owned platform API.");
        }
    }

    /// <summary>Verifies every presentation adapter owns one same-name top-level type.</summary>
    [Fact]
    public void PresentationAdapters_HaveOneMatchingTopLevelType()
    {
        string adapterRoot = Path.Combine(ApplicationRoot, "Presentation", "Adapters");
        string[] sourcePaths = EnumerateProductionFiles(adapterRoot, "*.cs").ToArray();

        Assert.NotEmpty(sourcePaths);
        foreach (string sourcePath in sourcePaths)
        {
            string expectedTypeName = Path.GetFileNameWithoutExtension(sourcePath);
            IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
            string actualTypeName = Assert.Single(ReadDeclaredTopLevelTypeNames(tokens));

            Assert.Equal(expectedTypeName, actualTypeName);
        }
    }

    /// <summary>Verifies ViewModel depends only on model and presentation-facing contracts.</summary>
    [Fact]
    public void ViewModels_DoNotReferenceServiceNamespace()
    {
        string viewModelRoot = Path.Combine(ApplicationRoot, "ViewModel");

        foreach (string sourcePath in EnumerateProductionFiles(viewModelRoot, "*.cs"))
        {
            Assert.DoesNotContain(
                "ClashSharp.Service",
                File.ReadAllText(sourcePath),
                StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies XAML code-behind never resolves process-wide services directly.</summary>
    [Fact]
    public void ViewCodeBehind_DoesNotUseServiceLocatorSingletons()
    {
        string viewRoot = Path.Combine(ApplicationRoot, "View");

        foreach (string sourcePath in EnumerateProductionFiles(viewRoot, "*.xaml.cs"))
        {
            IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
            string[] singletonOwners = ReadInstanceMemberOwners(tokens).ToArray();

            Assert.True(
                singletonOwners.Length == 0,
                $"{Path.GetRelativePath(ApplicationRoot, sourcePath)} resolves global instances: "
                + string.Join(", ", singletonOwners)
                + ". Resolve them in a Presentation/Composition boundary and inject a narrow contract.");
        }
    }

    /// <summary>Verifies XAML code-behind receives service behavior through presentation composition.</summary>
    [Fact]
    public void ViewCodeBehind_DoesNotReferenceConcreteServiceNamespace()
    {
        string viewRoot = Path.Combine(ApplicationRoot, "View");

        foreach (string sourcePath in EnumerateProductionFiles(viewRoot, "*.xaml.cs"))
        {
            string source = File.ReadAllText(sourcePath);

            Assert.DoesNotContain(
                "ClashSharp.Service",
                source,
                StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies view models use singleton syntax only for stateless null-object defaults.</summary>
    [Fact]
    public void ViewModels_DoNotResolveConcreteApplicationServices()
    {
        HashSet<string> allowedNullObjectSingletons = new(StringComparer.Ordinal)
        {
            "AlwaysAvailableMihomoServiceController",
            "EmptyProxyRuntimeController",
            "NoMasterControlApplicationActionDispatcher",
            "NoShellRestartState",
            "UnavailableMasterControlRuntime",
            "UnavailableMasterControlTrayStatus",
        };
        string viewModelRoot = Path.Combine(ApplicationRoot, "ViewModel");

        foreach (string sourcePath in EnumerateProductionFiles(viewModelRoot, "*.cs"))
        {
            IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
            string[] concreteSingletonOwners = ReadInstanceMemberOwners(tokens)
                .Where(owner => !allowedNullObjectSingletons.Contains(owner))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                concreteSingletonOwners.Length == 0,
                $"{Path.GetRelativePath(ApplicationRoot, sourcePath)} resolves concrete global services: "
                + string.Join(", ", concreteSingletonOwners)
                + ". View models must receive behavior through constructor dependencies.");
        }
    }

    /// <summary>Verifies infrastructure clients are not constructed inside view models.</summary>
    [Fact]
    public void ViewModels_DoNotConstructHttpClients()
    {
        string viewModelRoot = Path.Combine(ApplicationRoot, "ViewModel");

        foreach (string sourcePath in EnumerateProductionFiles(viewModelRoot, "*.cs"))
        {
            IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
            for (int index = 0; index < tokens.Count - 1; index++)
            {
                Assert.False(
                    string.Equals(tokens[index], "new", StringComparison.Ordinal)
                    && string.Equals(tokens[index + 1], "HttpClient", StringComparison.Ordinal),
                    $"{Path.GetRelativePath(ApplicationRoot, sourcePath)} constructs HttpClient; "
                    + "network I/O belongs behind an injected infrastructure dependency.");
            }
        }
    }

    /// <summary>Verifies page view models remain split into semantic, single-primary-type files.</summary>
    [Fact]
    public void PageViewModelFiles_HaveOneMatchingPrimaryType()
    {
        string viewModelRoot = Path.Combine(ApplicationRoot, "ViewModel");
        string[] primaryTypeNames =
        [
            "AboutViewModel",
            "ConfigurationProfileDisplay",
            "ConnectionsViewModel",
            "EmptyProxyRuntimeController",
            "IAboutCore",
            "IDiagnosticsLog",
            "IDisplayPageLocalization",
            "IMasterControlActions",
            "IMasterHeroStatusLayoutService",
            "IMasterInfoTileLayoutService",
            "IModelDisplayMapper",
            "IProxiesLocalization",
            "IProxiesLog",
            "IProxyLatencyTester",
            "IProxyNodeCatalog",
            "IProxyRuntimeController",
            "IRuleCatalog",
            "IStatisticsProfiles",
            "IStatisticsStore",
            "IUriLauncher",
            "IWindowsDiagnosticsClient",
            "LinksViewModel",
            "LogRecordDisplay",
            "LogsViewModel",
            "MainWindowViewModel",
            "MasterControlViewModel",
            "MihomoProviderResourceDisplay",
            "MihomoProxyGroupDisplay",
            "ModelDisplayMapper",
            "ProfileSubscriptionLinkDisplay",
            "ProfilesViewModel",
            "ProxyGroupSelectionRequest",
            "ProxyNodeDisplay",
            "RulePreviewDisplay",
            "RulesViewModel",
            "SettingsDiagnosticsViewModel",
            "SettingsDiagnosticStatus",
            "SettingsViewModel",
            "StatisticsSummary",
            "StatisticsViewModel",
        ];

        foreach (string primaryTypeName in primaryTypeNames)
        {
            string sourcePath = Path.Combine(viewModelRoot, primaryTypeName + ".cs");
            Assert.True(
                File.Exists(sourcePath),
                $"ViewModel/{primaryTypeName}.cs must contain the {primaryTypeName} responsibility.");

            IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(sourcePath));
            string actualTypeName = Assert.Single(ReadDeclaredTopLevelTypeNames(tokens));
            Assert.Equal(primaryTypeName, actualTypeName);
        }

        Assert.False(File.Exists(Path.Combine(viewModelRoot, "DisplayPageContracts.cs")));
        Assert.False(File.Exists(Path.Combine(viewModelRoot, "DisplayPageViewModels.cs")));
        Assert.False(File.Exists(Path.Combine(viewModelRoot, "ManagementPageViewModels.cs")));
    }

    /// <summary>Verifies data-backed page constructors do not perform their initial service reads.</summary>
    [Fact]
    public void DataBackedPageViewModels_LoadOnlyThroughExplicitLifecycle()
    {
        Dictionary<string, string> forbiddenConstructorCallByType = new(StringComparer.Ordinal)
        {
            ["ProfilesViewModel"] = "RefreshProfiles(",
            ["LinksViewModel"] = "RefreshLinks(",
            ["LogsViewModel"] = "RefreshLogs(",
            ["RulesViewModel"] = "GetRules(",
            ["StatisticsViewModel"] = "Refresh(",
            ["ProxiesViewModel"] = "_catalog.GetNodes(",
        };
        string viewModelRoot = Path.Combine(ApplicationRoot, "ViewModel");

        foreach ((string typeName, string forbiddenCall) in forbiddenConstructorCallByType)
        {
            string sourcePath = Path.Combine(viewModelRoot, typeName + ".cs");
            string source = File.ReadAllText(sourcePath);
            string constructorRegion = ReadConstructorRegion(source, typeName);

            Assert.DoesNotContain(forbiddenCall, constructorRegion, StringComparison.Ordinal);
            Assert.Contains(
                "Task LoadAsync(CancellationToken cancellationToken)",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "ViewModelLoadExecutor.ExecuteAsync(",
                source,
                StringComparison.Ordinal);
        }

        string settingsSource = File.ReadAllText(
            Path.Combine(viewModelRoot, "SettingsViewModel.cs"));
        string settingsConstructorRegion = ReadConstructorRegion(
            settingsSource,
            "SettingsViewModel");
        Assert.DoesNotContain(
            "Load();",
            settingsConstructorRegion,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_settings.",
            settingsConstructorRegion,
            StringComparison.Ordinal);

        string masterSource = File.ReadAllText(
            Path.Combine(viewModelRoot, "MasterControlViewModel.cs"));
        string masterConstructorRegion = ReadConstructorRegion(
            masterSource,
            "MasterControlViewModel");
        Assert.DoesNotContain("_settings.", masterConstructorRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("_infoTileLayout.GetLayout(", masterConstructorRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("_heroStatusLayout.GetLayout(", masterConstructorRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildInfoTiles();", masterConstructorRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildHeroStatusItems();", masterConstructorRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshTileValues();", masterConstructorRegion, StringComparison.Ordinal);
        Assert.Contains("EnsureInitialized();", masterSource, StringComparison.Ordinal);
    }

    /// <summary>Verifies data-backed pages load on Loaded and cancel their load on Unloaded.</summary>
    [Fact]
    public void DataBackedPageViews_UseCancellableLoadedLifecycle()
    {
        string[] pageNames = ["Profiles", "Links", "Logs", "Rules", "Statistics", "Proxies"];

        foreach (string pageName in pageNames)
        {
            string xaml = File.ReadAllText(
                Path.Combine(ApplicationRoot, "View", pageName + ".xaml"));
            string codeBehind = File.ReadAllText(
                Path.Combine(ApplicationRoot, "View", pageName + ".xaml.cs"));

            Assert.Contains("Loaded=\"Page_Loaded\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Unloaded=\"Page_Unloaded\"", xaml, StringComparison.Ordinal);
            Assert.Contains("PageLoadSession", codeBehind, StringComparison.Ordinal);
            Assert.Contains("_viewModel.LoadAsync", codeBehind, StringComparison.Ordinal);
            Assert.Contains("RunAsync(", codeBehind, StringComparison.Ordinal);
            Assert.Contains("_loadSession.Cancel()", codeBehind, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies every XAML class resolves to a same-name code-behind declaration.</summary>
    [Fact]
    public void XamlClasses_MatchCodeBehindNamespaceAndType()
    {
        foreach (string xamlPath in EnumerateProductionFiles(ApplicationRoot, "*.xaml"))
        {
            XDocument xaml = XDocument.Load(xamlPath, LoadOptions.SetLineInfo);
            XAttribute? classAttribute = xaml.Root?.Attribute(XName.Get("Class", XamlLanguageNamespace));
            if (classAttribute is null)
            {
                continue;
            }

            string qualifiedClassName = classAttribute.Value;
            int namespaceSeparator = qualifiedClassName.LastIndexOf('.');
            Assert.True(
                namespaceSeparator > 0 && namespaceSeparator < qualifiedClassName.Length - 1,
                $"{Path.GetRelativePath(ApplicationRoot, xamlPath)} has invalid x:Class '{qualifiedClassName}'.");

            string expectedNamespace = qualifiedClassName[..namespaceSeparator];
            string expectedTypeName = qualifiedClassName[(namespaceSeparator + 1)..];
            string codeBehindPath = xamlPath + ".cs";
            Assert.True(
                File.Exists(codeBehindPath),
                $"{Path.GetRelativePath(ApplicationRoot, xamlPath)} has no same-name code-behind.");

            IReadOnlyList<string> tokens = TokenizeCSharpSource(File.ReadAllText(codeBehindPath));
            string actualNamespace = Assert.Single(ReadDeclaredNamespaces(tokens));
            IReadOnlyList<string> declaredTypes = ReadDeclaredClassNames(tokens);

            Assert.Equal(expectedNamespace, actualNamespace);
            Assert.True(
                declaredTypes.Contains(expectedTypeName, StringComparer.Ordinal),
                $"{Path.GetRelativePath(ApplicationRoot, codeBehindPath)} does not declare "
                + $"the x:Class type {qualifiedClassName}.");
        }
    }

    private static IEnumerable<string> EnumerateProductionFiles(string root, string searchPattern)
    {
        return Directory
            .EnumerateFiles(root, searchPattern, SearchOption.AllDirectories)
            .Where(static path =>
            {
                string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    && !segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
            });
    }

    private static IEnumerable<string> ReadDeclaredNamespaces(IReadOnlyList<string> tokens)
    {
        for (int index = 0; index < tokens.Count; index++)
        {
            if (!string.Equals(tokens[index], "namespace", StringComparison.Ordinal))
            {
                continue;
            }

            List<string> nameParts = [];
            int cursor = index + 1;
            while (cursor < tokens.Count && IsIdentifier(tokens[cursor]))
            {
                nameParts.Add(tokens[cursor]);
                cursor++;
                if (cursor >= tokens.Count || !string.Equals(tokens[cursor], ".", StringComparison.Ordinal))
                {
                    break;
                }

                cursor++;
            }

            if (nameParts.Count > 0)
            {
                yield return string.Join('.', nameParts);
            }
        }
    }

    private static IReadOnlyList<string> ReadDeclaredClassNames(IReadOnlyList<string> tokens)
    {
        List<string> classNames = [];
        for (int index = 0; index < tokens.Count - 1; index++)
        {
            if (string.Equals(tokens[index], "class", StringComparison.Ordinal)
                && IsIdentifier(tokens[index + 1]))
            {
                classNames.Add(tokens[index + 1]);
            }
        }

        return classNames;
    }

    private static IReadOnlyList<string> ReadDeclaredTopLevelTypeNames(IReadOnlyList<string> tokens)
    {
        List<string> typeNames = [];
        int braceDepth = 0;
        for (int index = 0; index < tokens.Count; index++)
        {
            string token = tokens[index];
            if (string.Equals(token, "{", StringComparison.Ordinal))
            {
                braceDepth++;
                continue;
            }

            if (string.Equals(token, "}", StringComparison.Ordinal))
            {
                braceDepth--;
                continue;
            }

            if (braceDepth != 0
                || token is not ("class" or "interface" or "record" or "enum"))
            {
                continue;
            }

            int nameIndex = index + 1;
            if (string.Equals(token, "record", StringComparison.Ordinal)
                && nameIndex < tokens.Count
                && tokens[nameIndex] is "class" or "struct")
            {
                nameIndex++;
            }

            if (nameIndex < tokens.Count && IsIdentifier(tokens[nameIndex]))
            {
                typeNames.Add(tokens[nameIndex]);
            }
        }

        return typeNames;
    }

    private static IEnumerable<string> ReadInstanceMemberOwners(IReadOnlyList<string> tokens)
    {
        for (int index = 0; index < tokens.Count - 2; index++)
        {
            if (IsIdentifier(tokens[index])
                && string.Equals(tokens[index + 1], ".", StringComparison.Ordinal)
                && string.Equals(tokens[index + 2], "Instance", StringComparison.Ordinal))
            {
                yield return tokens[index];
            }
        }
    }

    private static string ReadConstructorRegion(string source, string typeName)
    {
        string signature = "public " + typeName + "(";
        int constructorStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(constructorStart >= 0, $"Could not find {typeName} constructor.");

        int nextMemberStart = source.IndexOf(
            "/// <summary>Gets",
            constructorStart,
            StringComparison.Ordinal);
        Assert.True(nextMemberStart > constructorStart, $"Could not bound {typeName} constructor.");
        return source[constructorStart..nextMemberStart];
    }

    private static string? FindUiFrameworkNamespace(IReadOnlyList<string> tokens)
    {
        for (int index = 0; index < tokens.Count - 2; index++)
        {
            if (string.Equals(tokens[index + 1], ".", StringComparison.Ordinal)
                && string.Equals(tokens[index + 2], "UI", StringComparison.Ordinal)
                && (string.Equals(tokens[index], "Microsoft", StringComparison.Ordinal)
                    || string.Equals(tokens[index], "Windows", StringComparison.Ordinal)))
            {
                return $"{tokens[index]}.UI";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> TokenizeCSharpSource(string source)
    {
        List<string> tokens = [];
        int index = 0;
        while (index < source.Length)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                continue;
            }

            if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && (source[index] != '*' || source[index + 1] != '/'))
                {
                    index++;
                }

                index = Math.Min(source.Length, index + 2);
                continue;
            }

            if (TrySkipLiteral(source, ref index))
            {
                continue;
            }

            if (IsIdentifierStart(source[index]))
            {
                int start = index;
                if (source[index] == '@')
                {
                    index++;
                }

                while (index < source.Length && IsIdentifierPart(source[index]))
                {
                    index++;
                }

                int valueStart = source[start] == '@' ? start + 1 : start;
                if (index > valueStart)
                {
                    tokens.Add(source[valueStart..index]);
                }

                continue;
            }

            if (source[index] is '.' or ';' or '{' or '}' or ':')
            {
                tokens.Add(source[index].ToString());
            }

            index++;
        }

        return tokens;
    }

    private static bool TrySkipLiteral(string source, ref int index)
    {
        if (source[index] == '\'')
        {
            index = SkipEscapedLiteral(source, index + 1, '\'');
            return true;
        }

        int quoteIndex = index;
        bool isVerbatim = false;
        while (quoteIndex < source.Length && source[quoteIndex] is '$' or '@')
        {
            isVerbatim |= source[quoteIndex] == '@';
            quoteIndex++;
        }

        if (quoteIndex >= source.Length || source[quoteIndex] != '"')
        {
            return false;
        }

        int quoteCount = 1;
        while (quoteIndex + quoteCount < source.Length && source[quoteIndex + quoteCount] == '"')
        {
            quoteCount++;
        }

        if (quoteCount >= 3)
        {
            index = SkipRawStringLiteral(source, quoteIndex + quoteCount, quoteCount);
            return true;
        }

        index = isVerbatim
            ? SkipVerbatimStringLiteral(source, quoteIndex + 1)
            : SkipEscapedLiteral(source, quoteIndex + 1, '"');
        return true;
    }

    private static int SkipEscapedLiteral(string source, int index, char delimiter)
    {
        while (index < source.Length)
        {
            if (source[index] == '\\')
            {
                index = Math.Min(source.Length, index + 2);
                continue;
            }

            if (source[index] == delimiter)
            {
                return index + 1;
            }

            index++;
        }

        return source.Length;
    }

    private static int SkipVerbatimStringLiteral(string source, int index)
    {
        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                index++;
                continue;
            }

            if (index + 1 < source.Length && source[index + 1] == '"')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return source.Length;
    }

    private static int SkipRawStringLiteral(string source, int index, int delimiterLength)
    {
        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                index++;
                continue;
            }

            int quoteCount = 1;
            while (index + quoteCount < source.Length && source[index + quoteCount] == '"')
            {
                quoteCount++;
            }

            if (quoteCount >= delimiterLength)
            {
                return index + delimiterLength;
            }

            index += quoteCount;
        }

        return source.Length;
    }

    private static bool IsIdentifier(string token)
    {
        return token.Length > 0 && token is not ("." or ";" or "{" or "}" or ":");
    }

    private static bool IsIdentifierStart(char value)
    {
        return value == '@' || value == '_' || char.IsLetter(value);
    }

    private static bool IsIdentifierPart(char value)
    {
        return value == '_' || char.IsLetterOrDigit(value);
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

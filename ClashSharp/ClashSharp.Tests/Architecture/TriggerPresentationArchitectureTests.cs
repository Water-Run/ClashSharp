using System.Text.RegularExpressions;

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the triggers page composition and presentation-layer ownership boundaries.</summary>
public sealed class TriggerPresentationArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ApplicationRoot = Path.Combine(
        RepositoryRoot,
        "ClashSharp",
        "ClashSharp");

    /// <summary>Verifies the page receives explicit state and semantic navigation.</summary>
    [Fact]
    public void TriggersView_ReceivesDependenciesWithoutResolvingHostOrServices()
    {
        string source = ReadApplicationSource("View", "Triggers.xaml.cs");

        Assert.Contains("internal Triggers(TriggersPageDependencies dependencies)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public Triggers()", source, StringComparison.Ordinal);
        Assert.Contains("_openLogs = dependencies.OpenLogs;", source, StringComparison.Ordinal);
        Assert.Contains("_openLogs();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Frame.Navigate", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Instance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using ClashSharp.Service;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using ClashSharp.Hosting.Compatibility;", source, StringComparison.Ordinal);
        Assert.Contains("CancellationToken cancellationToken = lifetime.Token;", source, StringComparison.Ordinal);
        Assert.Contains(
            "ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken)",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>Verifies trigger presentation is an injected AppHost-owned factory.</summary>
    [Fact]
    public void TriggersComposition_UsesInjectedHostOwnedFactory()
    {
        string composition = ReadApplicationSource(
            "Presentation",
            "Composition",
            "TriggersPageComposition.cs");
        string factory = ReadApplicationSource(
            "Presentation",
            "Composition",
            "TriggerPresentationFactory.cs");
        string host = ReadApplicationSource("AppHost", "ClashSharpAppHostFactory.cs");

        Assert.Contains("context.TriggerPresentation.CreateViewModel(", composition, StringComparison.Ordinal);
        Assert.Contains("context.ErrorSink", composition, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<TriggerPresentationFactory>();", host, StringComparison.Ordinal);
        Assert.Contains("ITriggerDefinitionStore store", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("static TriggerPresentationFactory", factory, StringComparison.Ordinal);
        Assert.DoesNotContain(".Instance", factory, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            ApplicationRoot,
            "AppHost",
            "Startup",
            "TriggerPresentationStartupStep.cs")));
    }

    /// <summary>Verifies trigger presentation files retain one matching primary type per source file.</summary>
    [Fact]
    public void TriggerPresentationFiles_HaveOneMatchingPrimaryType()
    {
        string[] relativePaths =
        [
            "ViewModel/ITriggerPresentationSettings.cs",
            "ViewModel/TriggerActionEditorViewModel.cs",
            "ViewModel/TriggerConditionEditorViewModel.cs",
            "ViewModel/TriggerConditionTemplate.cs",
            "ViewModel/TriggerEditorOption.cs",
            "ViewModel/TriggerEditorSaveResult.cs",
            "ViewModel/TriggerEditorViewModel.cs",
            "ViewModel/TriggersViewModel.cs",
            "ViewModel/TriggerTaskItemViewModel.cs",
            "Presentation/Composition/TriggersPageComposition.cs",
            "Presentation/Composition/TriggersPageDependencies.cs",
            "Presentation/Composition/TriggerPresentationFactory.cs",
            "Presentation/Composition/TriggerPresentationSummary.cs",
        ];
        Regex typeDeclaration = new(
            @"^\s*(?:public|internal)\s+(?:(?:static|sealed|readonly|partial)\s+)*(?:class|interface|record(?:\s+struct)?|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        foreach (string relativePath in relativePaths)
        {
            string source = File.ReadAllText(Path.Combine(
                ApplicationRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Match declaration = Assert.Single(typeDeclaration.Matches(source).Cast<Match>());
            Assert.Equal(
                Path.GetFileNameWithoutExtension(relativePath),
                declaration.Groups["name"].Value);
            Assert.DoesNotContain("#nullable enable", source, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies async-void methods in the page remain platform event handlers only.</summary>
    [Fact]
    public void TriggersView_UsesAsyncVoidOnlyForRoutedEventHandlers()
    {
        string source = ReadApplicationSource("View", "Triggers.xaml.cs");
        MatchCollection declarations = Regex.Matches(
            source,
            @"private\s+async\s+void\s+(?<name>\w+)\s*\((?<parameters>[^)]*)\)",
            RegexOptions.CultureInvariant);

        Assert.NotEmpty(declarations);
        Assert.All(declarations.Cast<Match>(), declaration =>
        {
            string methodName = declaration.Groups["name"].Value;
            Assert.True(
                string.Equals(methodName, "OnLoaded", StringComparison.Ordinal)
                || methodName.EndsWith("Button_Click", StringComparison.Ordinal)
                || methodName.EndsWith("Toggle_Toggled", StringComparison.Ordinal),
                $"{methodName} is not a routed-event handler.");
            Assert.Equal(
                "object sender, RoutedEventArgs args",
                declaration.Groups["parameters"].Value.Trim());
        });
    }

    private static string ReadApplicationSource(params string[] segments)
    {
        return File.ReadAllText(Path.Combine([ApplicationRoot, .. segments]));
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

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the main shell's MVVM and composition-root boundary.</summary>
public sealed class MainWindowCompositionArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ApplicationRoot = Path.Combine(
        RepositoryRoot,
        "ClashSharp",
        "ClashSharp");

    /// <summary>Verifies the window code-behind never locates global services.</summary>
    [Fact]
    public void MainWindow_DoesNotUseServiceLocator()
    {
        string source = ReadApplicationSource("MainWindow.xaml.cs");

        Assert.DoesNotContain(".Instance", source, StringComparison.Ordinal);
    }

    /// <summary>Verifies runtime service graphs are constructed only by the shell composition root.</summary>
    [Fact]
    public void MainWindow_RuntimeObjectsComeFromCompositionRoot()
    {
        string window = ReadApplicationSource("MainWindow.xaml.cs");
        string composition = ReadApplicationSource(
            "Presentation",
            "Composition",
            "MainWindowComposition.cs");

        string[] forbiddenWindowConstructions =
        [
            "new MainWindowViewModel(",
            "new ShellLocalizationAdapter(",
            "new ShellRestartStateAdapter(",
            "new SystemTrayService(",
        ];

        Assert.All(
            forbiddenWindowConstructions,
            construction => Assert.DoesNotContain(construction, window, StringComparison.Ordinal));
        Assert.Contains("MainWindowComposition.Create()", window, StringComparison.Ordinal);
        Assert.Contains("_composition.CreateRuntime()", window, StringComparison.Ordinal);
        Assert.Contains("new MainWindowViewModel(", composition, StringComparison.Ordinal);
        Assert.Contains("new SystemTrayService(", composition, StringComparison.Ordinal);
    }

    /// <summary>Verifies the shell composition root remains in the presentation composition namespace.</summary>
    [Fact]
    public void MainWindowComposition_IsClassifiedAsPresentationComposition()
    {
        string source = ReadApplicationSource(
            "Presentation",
            "Composition",
            "MainWindowComposition.cs");

        Assert.Contains(
            "namespace ClashSharp.Presentation.Composition;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal sealed class MainWindowComposition",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>Guards the connections page against becoming an orphaned implementation again.</summary>
    [Fact]
    public void ConnectionsPage_IsReachableAndOwnsItsInitialLoadLifetime()
    {
        string windowXaml = ReadApplicationSource("MainWindow.xaml");
        string windowCode = ReadApplicationSource("MainWindow.xaml.cs");
        string composition = ReadApplicationSource(
            "Presentation",
            "Composition",
            "MainWindowComposition.cs");
        string tray = ReadApplicationSource("Service", "TrayMenuStateBuilder.cs");
        string pageXaml = ReadApplicationSource("View", "Connections.xaml");
        string pageCode = ReadApplicationSource("View", "Connections.xaml.cs");

        Assert.Contains("Tag=\"Connections\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("[\"Connections\"] = typeof(View.Connections)", composition, StringComparison.Ordinal);
        Assert.Contains("\"Connections\" => NavConnectionsItem", windowCode, StringComparison.Ordinal);
        Assert.Contains("new(\"Connections\", getString(\"Nav.Connections\"))", tray, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"Page_Loaded\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("Unloaded=\"Page_Unloaded\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("_loadSession.RunAsync", pageCode, StringComparison.Ordinal);
        Assert.Contains("_loadSession.Cancel()", pageCode, StringComparison.Ordinal);
        Assert.Contains("_streamSession.RunAsync(_viewModel.WatchConnectionsAsync)", pageCode, StringComparison.Ordinal);
        Assert.Contains("_streamSession.Cancel()", pageCode, StringComparison.Ordinal);
    }

    /// <summary>Guards tray callbacks against continuing through a closed window runtime.</summary>
    [Fact]
    public void MainWindow_TrayAsyncCallbacksSnapshotRuntimeAndObserveWindowLifetime()
    {
        string source = ReadApplicationSource("MainWindow.xaml.cs");
        string applyMode = ReadMethodBoundary(
            source,
            "private async void ApplyModeFromTray(",
            "private async void SetTransparentProxyFromTray(");
        string setTransparentProxy = ReadMethodBoundary(
            source,
            "private async void SetTransparentProxyFromTray(",
            "private async Task NotifyAndTriggerModeAppliedAsync(");

        Assert.All(
            new[] { applyMode, setTransparentProxy },
            boundary =>
            {
                Assert.Contains(
                    "MainWindowComposition.Runtime? runtime = _runtime;",
                    boundary,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "CancellationToken cancellationToken = _windowLifetime.Token;",
                    boundary,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "ExceptionGraphClassifier.IsCallerCancellation",
                    boundary,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "ReportShellOperationFailureAsync(",
                    boundary,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "CancellationToken.None",
                    boundary,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("Runtime.", boundary, StringComparison.Ordinal);
            });

        Assert.Contains(
            "PublishProxyModeAppliedAsync(mode, cancellationToken)",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadApplicationSource(params string[] relativeSegments)
    {
        return File.ReadAllText(Path.Combine([ApplicationRoot, .. relativeSegments]));
    }

    private static string ReadMethodBoundary(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method marker '{startMarker}'.");
        Assert.True(end > start, $"Could not find method boundary '{endMarker}'.");
        return source[start..end];
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

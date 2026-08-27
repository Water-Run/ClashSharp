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

    /// <summary>Verifies AppHost owns the interactive shell graph and the window consumes it.</summary>
    [Fact]
    public void MainWindow_RuntimeObjectsComeFromAppHostCompositionRoot()
    {
        string window = ReadApplicationSource("MainWindow.xaml.cs");
        string app = ReadApplicationSource("App.xaml.cs");
        string host = ReadApplicationSource("AppHost", "ClashSharpAppHostFactory.cs");
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
        Assert.Contains("MainWindowComposition.CreateStartupShell(", app, StringComparison.Ordinal);
        Assert.Contains("MainWindowComposition.Runtime runtime", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_composition.CreateRuntime()", window, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<PageCompositionContext>();", host, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<ShellNavigationService>();", host, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<IPageFactory, ApplicationPageFactory>();", host, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<MainWindowComposition.Runtime>();", host, StringComparison.Ordinal);
        Assert.Contains("new MainWindowViewModel(", composition, StringComparison.Ordinal);
        Assert.Contains("new SystemTrayService(", composition, StringComparison.Ordinal);
    }

    /// <summary>Verifies route activation is factory-owned and no page uses framework type activation.</summary>
    [Fact]
    public void ShellNavigation_UsesTypedRoutesAndExplicitPageFactory()
    {
        string window = ReadApplicationSource("MainWindow.xaml.cs");
        string pageFactory = ReadApplicationSource(
            "Presentation",
            "Navigation",
            "ApplicationPageFactory.cs");
        string viewModel = ReadApplicationSource("ViewModel", "MainWindowViewModel.cs");

        Assert.Contains("ShellNavigationRequest request", window, StringComparison.Ordinal);
        Assert.Contains("runtime.PageFactory.Create(", window, StringComparison.Ordinal);
        Assert.Contains("ContentFrame.Content = page;", window, StringComparison.Ordinal);
        Assert.Contains("_navigationHistory", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Frame.Navigate", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolvePageType", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("IServiceProvider", pageFactory, StringComparison.Ordinal);
        Assert.DoesNotContain(".Instance", pageFactory, StringComparison.Ordinal);
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
        string pageFactory = ReadApplicationSource(
            "Presentation",
            "Navigation",
            "ApplicationPageFactory.cs");
        string tray = ReadApplicationSource("Service", "TrayMenuStateBuilder.cs");
        string pageXaml = ReadApplicationSource("View", "Connections.xaml");
        string pageCode = ReadApplicationSource("View", "Connections.xaml.cs");

        Assert.Contains("Tag=\"Connections\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("ShellRoute.Connections => new View.Connections(", pageFactory, StringComparison.Ordinal);
        Assert.Contains("ShellRoute.Connections => NavConnectionsItem", windowCode, StringComparison.Ordinal);
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

    /// <summary>Guards verified runtime and relevant setting changes against leaving the tray stale.</summary>
    [Fact]
    public void MainWindow_RefreshesTrayFromFilteredAndCoalescedStateNotifications()
    {
        string composition = ReadApplicationSource(
            "Presentation",
            "Composition",
            "MainWindowComposition.cs");
        string window = ReadApplicationSource("MainWindow.xaml.cs");
        string coordinator = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "ClashSharp.Application",
            "Network",
            "NetworkStateCoordinator.cs"));
        string settingsHandler = ReadMethodBoundary(
            composition,
            "private void Settings_SettingChanged(",
            "private void NetworkState_VerifiedStateChanged(");
        string trayHandler = ReadMethodBoundary(
            window,
            "private void OnTrayStateChanged(",
            "private async void ApplyModeFromTray(");

        Assert.Contains("public event EventHandler? VerifiedStateChanged", coordinator, StringComparison.Ordinal);
        Assert.Contains("PublishVerifiedState(null)", coordinator, StringComparison.Ordinal);
        Assert.Contains("PublishVerifiedState(state)", coordinator, StringComparison.Ordinal);
        Assert.Contains("_settings.SettingChanged += Settings_SettingChanged", composition, StringComparison.Ordinal);
        Assert.Contains("_settings.SettingChanged -= Settings_SettingChanged", composition, StringComparison.Ordinal);
        Assert.Contains(
            "_networkState.VerifiedStateChanged += NetworkState_VerifiedStateChanged",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "_networkState.VerifiedStateChanged -= NetworkState_VerifiedStateChanged",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("_mihomoCore.UnexpectedExit += MihomoCore_UnexpectedExit", composition, StringComparison.Ordinal);
        Assert.Contains("_mihomoCore.UnexpectedExit -= MihomoCore_UnexpectedExit", composition, StringComparison.Ordinal);
        Assert.Contains("TrayRelevantSettingKeys.Contains(e.Key)", settingsHandler, StringComparison.Ordinal);
        Assert.Contains("SettingsRegistry.Keys.CurrentMode.Value", composition, StringComparison.Ordinal);
        Assert.Contains("SettingsRegistry.Keys.TransparentProxyEnabled.Value", composition, StringComparison.Ordinal);
        Assert.Contains("SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon.Value", composition, StringComparison.Ordinal);
        Assert.Contains("SettingsRegistry.Keys.TrayVisibleFeatureIds.Value", composition, StringComparison.Ordinal);
        Assert.Contains("_runtime.TrayStateChanged += OnTrayStateChanged", window, StringComparison.Ordinal);
        Assert.Contains("runtime.TrayStateChanged -= OnTrayStateChanged", window, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _trayRefreshQueued, 1)", trayHandler, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _trayRefreshQueued, 0)", trayHandler, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue", trayHandler, StringComparison.Ordinal);
        Assert.Contains("RefreshTrayMenuPreservingReachability()", trayHandler, StringComparison.Ordinal);
        Assert.Contains("_mihomoCore.IsRunning", composition, StringComparison.Ordinal);
        Assert.Contains("serviceStatus.IsReady", composition, StringComparison.Ordinal);
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

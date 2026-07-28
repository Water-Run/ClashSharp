namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the one-modal-surface-per-window presentation contract.</summary>
public sealed class DialogPresentationArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ApplicationRoot = Path.Combine(
        RepositoryRoot,
        "ClashSharp",
        "ClashSharp");

    /// <summary>Verifies application call sites cannot bypass the window dialog coordinator.</summary>
    [Fact]
    public void ContentDialogCallSites_UseManagedPresentation()
    {
        string coordinatorPath = Path.Combine(
            ApplicationRoot,
            "Presentation",
            "Dialogs",
            "WindowDialogCoordinator.cs");
        string[] bypasses = Directory
            .EnumerateFiles(ApplicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, coordinatorPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path))
            .Where(path => File.ReadAllText(path).Contains(".ShowAsync()", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(ApplicationRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(bypasses);
    }

    /// <summary>Verifies custom centered overlays share the same per-window admission gate.</summary>
    [Fact]
    public void CenteredOverlay_UsesWindowDialogCoordinator()
    {
        string overlayPath = Path.Combine(
            ApplicationRoot,
            "Presentation",
            "Dialogs",
            "CenteredDialogOverlay.cs");
        string source = File.ReadAllText(overlayPath);

        Assert.Contains("WindowDialogCoordinator.TryShowOverlayAsync(", source, StringComparison.Ordinal);
        Assert.Contains("rootPanel.Unloaded += CompleteForVisualTreeTeardown", source, StringComparison.Ordinal);
        Assert.Contains("window.Closed += CompleteForWindowClose", source, StringComparison.Ordinal);
        Assert.Contains("finally", source, StringComparison.Ordinal);
        Assert.Contains("rootPanel.Unloaded -= CompleteForVisualTreeTeardown", source, StringComparison.Ordinal);
        Assert.Contains("window.Closed -= CompleteForWindowClose", source, StringComparison.Ordinal);
        Assert.Contains("rootPanel.Children.Remove(overlay)", source, StringComparison.Ordinal);
    }

    /// <summary>Verifies conflict detection, presentation, and repair share the owning visual lifetime.</summary>
    [Fact]
    public void StartupConflictFlow_IsAsyncAndCancellableFromEveryOwner()
    {
        string presenter = ReadApplicationSource(
            "Presentation",
            "Dialogs",
            "StartupConflictDialogPresenter.cs");
        string overlay = ReadApplicationSource(
            "Presentation",
            "Dialogs",
            "CenteredDialogOverlay.cs");
        string coordinator = ReadApplicationSource(
            "Presentation",
            "Dialogs",
            "WindowDialogCoordinator.cs");
        string masterComposition = ReadApplicationSource(
            "Presentation",
            "Composition",
            "MasterControlPageComposition.cs");
        string masterPage = ReadApplicationSource("View", "MasterControl.xaml.cs");
        string settingsComposition = ReadApplicationSource(
            "Presentation",
            "Composition",
            "SettingsPageComposition.cs");
        string settingsPage = ReadApplicationSource("View", "Settings.xaml.cs");
        string settingsViewModel = ReadApplicationSource("ViewModel", "SettingsViewModel.cs");
        string mainWindow = ReadApplicationSource("MainWindow.xaml.cs");

        Assert.Contains("CancellationToken cancellationToken", presenter, StringComparison.Ordinal);
        Assert.Contains("issue.RepairAsync(cancellationToken)", presenter, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", presenter, StringComparison.Ordinal);
        Assert.Contains("dialogLifetime.Cancel()", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", presenter, StringComparison.Ordinal);

        Assert.Contains("CancellationToken cancellationToken", overlay, StringComparison.Ordinal);
        Assert.Contains("closed.TrySetCanceled(cancellationToken)", overlay, StringComparison.Ordinal);
        Assert.Contains("Func<CancellationToken, Task> showAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("await showAsync(cancellationToken)", coordinator, StringComparison.Ordinal);

        Assert.Contains(
            "Func<Microsoft.UI.Xaml.XamlRoot, CancellationToken, Task> ShowStartupConflicts",
            masterComposition,
            StringComparison.Ordinal);
        Assert.Contains(".CheckConflictsAsync(settings.MixedPort, cancellationToken)", masterComposition, StringComparison.Ordinal);
        Assert.DoesNotContain(".CheckConflicts(settings.MixedPort)", masterComposition, StringComparison.Ordinal);
        Assert.Contains("_showStartupConflicts(GetDialogXamlRoot(), cancellationToken)", masterPage, StringComparison.Ordinal);
        Assert.Contains("_pageLifetime?.Token", masterPage, StringComparison.Ordinal);

        Assert.Contains(
            "checkStartupConflictsAsync: StartupConflictDetectionService.Instance.CheckConflictsAsync",
            settingsComposition,
            StringComparison.Ordinal);
        Assert.Contains(
            "Task<IReadOnlyList<StartupConflictIssue>> CheckStartupConflictsAsync(",
            settingsViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _viewModel.CheckStartupConflictsAsync(cancellationToken)",
            settingsPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_viewModel.CheckStartupConflicts()", settingsPage, StringComparison.Ordinal);
        Assert.Contains("_windowLifetime.Token", mainWindow, StringComparison.Ordinal);
    }

    /// <summary>Verifies startup guidance has one cancellable ContentDialog presentation owner.</summary>
    [Fact]
    public void StartupGuide_UsesPreCollectedChecksAndManagedContentDialog()
    {
        string component = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "Components",
            "StartupGuideDialog.xaml.cs"));
        string presenter = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "Presentation",
            "Dialogs",
            "StartupGuidePresenter.cs"));
        string service = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "Service",
            "StartupCheckService.cs"));
        string coordinator = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "Presentation",
            "Dialogs",
            "WindowDialogCoordinator.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(ApplicationRoot, "MainWindow.xaml.cs"));
        string masterControl = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "View",
            "MasterControl.xaml.cs"));
        string settings = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "View",
            "Settings.xaml.cs"));

        Assert.Contains("IReadOnlyList<StartupCheckItem> checks", component, StringComparison.Ordinal);
        Assert.Contains("Func<string, string> getString", component, StringComparison.Ordinal);
        Assert.DoesNotContain(".Instance", component, StringComparison.Ordinal);
        Assert.DoesNotContain("using ClashSharp.Service;", component, StringComparison.Ordinal);
        Assert.DoesNotContain("GetChecks", component, StringComparison.Ordinal);
        Assert.DoesNotContain("CenteredDialogOverlay", component, StringComparison.Ordinal);

        Assert.Contains("_checks.GetChecksAsync(cancellationToken)", presenter, StringComparison.Ordinal);
        Assert.Contains(
            "WindowDialogCoordinator.ShowAsync(dialog, cancellationToken)",
            presenter,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CenteredDialogOverlay", presenter, StringComparison.Ordinal);

        Assert.Contains("Task.Run(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", service, StringComparison.Ordinal);
        Assert.DoesNotContain("static StartupCheckService Instance", service, StringComparison.Ordinal);
        Assert.Contains("dialog.Hide()", coordinator, StringComparison.Ordinal);
        Assert.Contains(
            "throw new OperationCanceledException(cancellationToken)",
            coordinator,
            StringComparison.Ordinal);
        Assert.DoesNotContain("StartupGuideDialog", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("StartupGuideDialog", masterControl, StringComparison.Ordinal);
        Assert.DoesNotContain("StartupGuideDialog", settings, StringComparison.Ordinal);
        Assert.Contains("_windowLifetime.Token", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_pageLifetime?.Token", masterControl, StringComparison.Ordinal);
        Assert.Contains("_pageLifetime?.Token", settings, StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string path)
    {
        string relativePath = Path.GetRelativePath(ApplicationRoot, path);
        string[] segments = relativePath.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string ReadApplicationSource(params string[] pathSegments)
    {
        string[] fullPathSegments = [ApplicationRoot, .. pathSegments];
        return File.ReadAllText(Path.Combine(fullPathSegments));
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

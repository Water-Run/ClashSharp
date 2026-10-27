using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the main window navigation view model.</summary>
public sealed class MainWindowViewModelTests
{
    [Fact]
    public void TaskbarNotice_IsDismissibleAndRelocalizesWithoutReopening()
    {
        FakeShellLocalization localization = new();
        using MainWindowViewModel viewModel = new(localization);
        Assert.False(viewModel.IsInstallationPinUnavailableVisible);
        viewModel.IsInstallationPinUnavailableVisible = true;
        Assert.Equal("Pin to taskbar", viewModel.InstallationPinTitleText);
        viewModel.IsInstallationPinUnavailableVisible = false;
        List<string?> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        localization.Strings["Installation.Taskbar.Title"] = "固定到任务栏";
        localization.Strings["Installation.Taskbar.Unavailable"] = "可手动固定";
        localization.RaiseLanguageChanged();
        Assert.False(viewModel.IsInstallationPinUnavailableVisible);
        Assert.Equal("固定到任务栏", viewModel.InstallationPinTitleText);
        Assert.Equal("可手动固定", viewModel.InstallationPinUnavailableText);
        Assert.Contains(nameof(viewModel.InstallationPinTitleText), changed);
        Assert.Contains(nameof(viewModel.InstallationPinUnavailableText), changed);
    }

    [Fact]
    public void RecoveryNotice_UsesTheActivePageWithoutClearingFailureState()
    {
        using MainWindowViewModel viewModel = new(new FakeShellLocalization());
        bool displayed = viewModel.IsSettingsRecoveryVisible;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.IsSettingsRecoveryVisible)) { displayed = viewModel.IsSettingsRecoveryVisible; }
        };
        viewModel.HasSettingsApplicationFailures = true;
        Assert.True(displayed);

        viewModel.IsSettingsPageActive = true;
        Assert.False(displayed);
        Assert.True(viewModel.HasSettingsApplicationFailures);
        viewModel.IsSettingsPageActive = false;
        Assert.True(displayed);

        viewModel.IsSettingsPageActive = true;
        viewModel.HasSettingsApplicationFailures = false;
        viewModel.IsSettingsPageActive = false;
        Assert.False(displayed);
    }

    /// <summary>Verifies localized navigation labels are loaded during construction.</summary>
    [Fact]
    public void Constructor_LoadsNavigationLabels()
    {
        FakeShellLocalization localization = new();

        MainWindowViewModel viewModel = new(localization);

        Assert.Equal("Master", viewModel.MasterControlText);
        Assert.Equal("Proxies", viewModel.ProxiesText);
        Assert.Equal("Nodes", viewModel.ProxyNodesText);
        Assert.Equal("Profiles", viewModel.ProfilesText);
        Assert.Equal("Links", viewModel.LinksText);
        Assert.Equal("Rules", viewModel.RulesText);
        Assert.Equal("Triggers", viewModel.TriggersText);
        Assert.Equal("Connections", viewModel.ConnectionsText);
        Assert.Equal("Statistics", viewModel.StatisticsText);
        Assert.Equal("About", viewModel.AboutText);
        Assert.Equal("Settings", viewModel.SettingsText);
    }

    /// <summary>Verifies restart-pending settings add and remove the shell settings marker dynamically.</summary>
    [Fact]
    public void RestartPendingChanged_UpdatesSettingsNavigationMarker()
    {
        FakeShellLocalization localization = new();
        FakeShellRestartState restartState = new() { IsRestartPending = true };
        MainWindowViewModel viewModel = new(localization, restartState);
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.Equal("Settings*", viewModel.SettingsText);

        restartState.IsRestartPending = false;
        restartState.RaiseRestartPendingChanged();

        Assert.Equal("Settings", viewModel.SettingsText);
        Assert.Contains(nameof(MainWindowViewModel.SettingsText), changedProperties);
    }

    /// <summary>Verifies language change notifications refresh labels and raise property changes.</summary>
    [Fact]
    public void LanguageChanged_RefreshesNavigationLabels()
    {
        FakeShellLocalization localization = new();
        MainWindowViewModel viewModel = new(localization);
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        localization.Strings["Nav.MasterControl"] = "主控";
        localization.RaiseLanguageChanged();

        Assert.Equal("主控", viewModel.MasterControlText);
        Assert.Contains(nameof(MainWindowViewModel.MasterControlText), changedProperties);
    }

    /// <summary>Fake localization provider for shell tests.</summary>
    private sealed class FakeShellLocalization : IShellLocalization
    {
        /// <summary>Gets mutable strings keyed by localization resource name.</summary>
        /// <value>Localization test dictionary.</value>
        public Dictionary<string, string> Strings { get; } = new()
        {
            ["Nav.MasterControl"] = "Master",
            ["Nav.Proxies"] = "Proxies",
            ["Nav.ProxyNodes"] = "Nodes",
            ["Nav.Profiles"] = "Profiles",
            ["Nav.Links"] = "Links",
            ["Nav.Rules"] = "Rules",
            ["Nav.Triggers"] = "Triggers",
            ["Nav.Connections"] = "Connections",
            ["Nav.Statistics"] = "Statistics",
            ["Nav.About"] = "About",
            ["Nav.Settings"] = "Settings",
            ["Installation.Taskbar.Title"] = "Pin to taskbar",
            ["Installation.Taskbar.Unavailable"] = "Manual pinning guidance",
        };

        /// <summary>Occurs when the fake language changes.</summary>
        /// <remarks>Raised synchronously by <see cref="RaiseLanguageChanged"/>.</remarks>
        public event EventHandler? LanguageChanged;

        /// <summary>Gets a localized string for a key.</summary>
        /// <param name="key">Localization key. Must not be null.</param>
        /// <returns>Localized string for the key.</returns>
        public string GetString(string key)
        {
            return Strings[key];
        }

        /// <summary>Raises the fake language changed event.</summary>
        public void RaiseLanguageChanged()
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Fake restart-pending state source for shell tests.</summary>
    private sealed class FakeShellRestartState : IShellRestartState
    {
        public event EventHandler? RestartPendingChanged;

        public bool IsRestartPending { get; set; }

        public void RaiseRestartPendingChanged()
        {
            RestartPendingChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

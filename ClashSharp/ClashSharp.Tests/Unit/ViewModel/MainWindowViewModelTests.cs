/*
 * Main Window ViewModel Tests
 * Verifies navigation shell label and page-resolution behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/MainWindowViewModelTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the main window navigation view model.</summary>
public sealed class MainWindowViewModelTests
{
    /// <summary>Verifies localized navigation labels are loaded during construction.</summary>
    [Fact]
    public void Constructor_LoadsNavigationLabels()
    {
        FakeShellLocalization localization = new();

        MainWindowViewModel viewModel = new(localization, CreatePageMap());

        Assert.Equal("Master", viewModel.MasterControlText);
        Assert.Equal("Proxies", viewModel.ProxiesText);
        Assert.Equal("Nodes", viewModel.ProxyNodesText);
        Assert.Equal("Profiles", viewModel.ProfilesText);
        Assert.Equal("Links", viewModel.LinksText);
        Assert.Equal("Rules", viewModel.RulesText);
        Assert.Equal("Statistics", viewModel.StatisticsText);
        Assert.Equal("About", viewModel.AboutText);
        Assert.Equal("Settings", viewModel.SettingsText);
    }

    /// <summary>Verifies language change notifications refresh labels and raise property changes.</summary>
    [Fact]
    public void LanguageChanged_RefreshesNavigationLabels()
    {
        FakeShellLocalization localization = new();
        MainWindowViewModel viewModel = new(localization, CreatePageMap());
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        localization.Strings["Nav.MasterControl"] = "主控";
        localization.RaiseLanguageChanged();

        Assert.Equal("主控", viewModel.MasterControlText);
        Assert.Contains(nameof(MainWindowViewModel.MasterControlText), changedProperties);
    }

    /// <summary>Verifies known navigation tags resolve to configured page types.</summary>
    [Fact]
    public void ResolvePageType_KnownTag_ReturnsConfiguredPageType()
    {
        MainWindowViewModel viewModel = new(new FakeShellLocalization(), CreatePageMap());

        Type? pageType = viewModel.ResolvePageType("MasterControl");

        Assert.Equal(typeof(MasterPage), pageType);
    }

    /// <summary>Verifies unknown navigation tags do not resolve to a page.</summary>
    [Fact]
    public void ResolvePageType_UnknownTag_ReturnsNull()
    {
        MainWindowViewModel viewModel = new(new FakeShellLocalization(), CreatePageMap());

        Type? pageType = viewModel.ResolvePageType("Unknown");

        Assert.Null(pageType);
    }

    /// <summary>Creates the test navigation map.</summary>
    /// <returns>Navigation map keyed by shell tag.</returns>
    private static IReadOnlyDictionary<string, Type> CreatePageMap()
    {
        return new Dictionary<string, Type>
        {
            ["MasterControl"] = typeof(MasterPage),
            ["ProxyNodes"] = typeof(ProxyNodesPage),
        };
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
            ["Nav.Statistics"] = "Statistics",
            ["Nav.About"] = "About",
            ["Nav.Settings"] = "Settings",
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

    /// <summary>Placeholder page type used by shell navigation tests.</summary>
    private sealed class MasterPage;

    /// <summary>Placeholder page type used by shell navigation tests.</summary>
    private sealed class ProxyNodesPage;
}

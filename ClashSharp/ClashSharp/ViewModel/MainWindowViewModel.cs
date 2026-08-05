using System;
using System.Collections.Generic;

namespace ClashSharp.ViewModel;

/// <summary>Bindable view model for the main application shell.</summary>
/// <remarks>
/// Invariants: Navigation labels are non-null after construction and page resolution uses the injected immutable map.
/// Thread safety: Not thread-safe; intended for UI-thread shell binding.
/// Side effects: Subscribes to localization change notifications until disposed.
/// </remarks>
internal sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Localization provider used by navigation labels.</summary>
    private readonly IShellLocalization _localization;

    /// <summary>Restart-required state source used by the settings navigation marker.</summary>
    private readonly IShellRestartState _restartState;

    /// <summary>Navigation tag to page-type mapping.</summary>
    private readonly IReadOnlyDictionary<string, Type> _pageTypes;

    /// <summary>Backing field for <see cref="MasterControlText"/>.</summary>
    private string _masterControlText = string.Empty;

    /// <summary>Backing field for <see cref="ProxiesText"/>.</summary>
    private string _proxiesText = string.Empty;

    /// <summary>Backing field for <see cref="ProxyNodesText"/>.</summary>
    private string _proxyNodesText = string.Empty;

    /// <summary>Backing field for <see cref="ProfilesText"/>.</summary>
    private string _profilesText = string.Empty;

    /// <summary>Backing field for <see cref="LinksText"/>.</summary>
    private string _linksText = string.Empty;

    /// <summary>Backing field for <see cref="RulesText"/>.</summary>
    private string _rulesText = string.Empty;

    /// <summary>Backing field for <see cref="StatisticsText"/>.</summary>
    private string _statisticsText = string.Empty;

    /// <summary>Backing field for <see cref="ConnectionsText"/>.</summary>
    private string _connectionsText = string.Empty;

    /// <summary>Backing field for <see cref="TriggersText"/>.</summary>
    private string _triggersText = string.Empty;

    /// <summary>Backing field for <see cref="AboutText"/>.</summary>
    private string _aboutText = string.Empty;

    /// <summary>Backing field for <see cref="SettingsText"/>.</summary>
    private string _settingsText = string.Empty;

    /// <summary>Initializes a shell view model with localization and page mapping dependencies.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="pageTypes">Navigation tag to page-type mapping. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localization"/> or <paramref name="pageTypes"/> is null.</exception>
    public MainWindowViewModel(
        IShellLocalization localization,
        IReadOnlyDictionary<string, Type> pageTypes,
        IShellRestartState? restartState = null)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _pageTypes = pageTypes ?? throw new ArgumentNullException(nameof(pageTypes));
        _restartState = restartState ?? NoShellRestartState.Instance;
        _localization.LanguageChanged += OnLanguageChanged;
        _restartState.RestartPendingChanged += OnRestartPendingChanged;
        RefreshLocalizedText();
    }

    /// <summary>Gets the master-control navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string MasterControlText
    {
        get => _masterControlText;
        private set => SetProperty(ref _masterControlText, value);
    }

    /// <summary>Gets the proxy group navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string ProxiesText
    {
        get => _proxiesText;
        private set => SetProperty(ref _proxiesText, value);
    }

    /// <summary>Gets the proxy-node navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string ProxyNodesText
    {
        get => _proxyNodesText;
        private set => SetProperty(ref _proxyNodesText, value);
    }

    /// <summary>Gets the profiles navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string ProfilesText
    {
        get => _profilesText;
        private set => SetProperty(ref _profilesText, value);
    }

    /// <summary>Gets the subscription links navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string LinksText
    {
        get => _linksText;
        private set => SetProperty(ref _linksText, value);
    }

    /// <summary>Gets the rules navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string RulesText
    {
        get => _rulesText;
        private set => SetProperty(ref _rulesText, value);
    }

    /// <summary>Gets the statistics navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string StatisticsText
    {
        get => _statisticsText;
        private set => SetProperty(ref _statisticsText, value);
    }

    /// <summary>Gets the active-connections navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string ConnectionsText
    {
        get => _connectionsText;
        private set => SetProperty(ref _connectionsText, value);
    }

    /// <summary>Gets the triggers navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string TriggersText
    {
        get => _triggersText;
        private set => SetProperty(ref _triggersText, value);
    }

    /// <summary>Gets the about-page navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string AboutText
    {
        get => _aboutText;
        private set => SetProperty(ref _aboutText, value);
    }

    /// <summary>Gets the settings navigation label.</summary>
    /// <value>Localized label text; never null.</value>
    public string SettingsText
    {
        get => _settingsText;
        private set => SetProperty(ref _settingsText, value);
    }

    /// <summary>Resolves a navigation tag to a page type.</summary>
    /// <param name="tag">Navigation item tag. Must not be null.</param>
    /// <returns>The configured page type when the tag is known; otherwise null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is null.</exception>
    public Type? ResolvePageType(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return _pageTypes.TryGetValue(tag, out Type? pageType) ? pageType : null;
    }

    /// <summary>Unsubscribes from localization notifications.</summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _restartState.RestartPendingChanged -= OnRestartPendingChanged;
    }

    /// <summary>Refreshes all localized navigation labels.</summary>
    private void RefreshLocalizedText()
    {
        MasterControlText = _localization.GetString("Nav.MasterControl");
        ProxiesText = _localization.GetString("Nav.Proxies");
        ProxyNodesText = _localization.GetString("Nav.ProxyNodes");
        ProfilesText = _localization.GetString("Nav.Profiles");
        LinksText = _localization.GetString("Nav.Links");
        RulesText = _localization.GetString("Nav.Rules");
        TriggersText = _localization.GetString("Nav.Triggers");
        ConnectionsText = _localization.GetString("Nav.Connections");
        StatisticsText = _localization.GetString("Nav.Statistics");
        AboutText = _localization.GetString("Nav.About");
        SettingsText = _restartState.IsRestartPending
            ? $"{_localization.GetString("Nav.Settings")}*"
            : _localization.GetString("Nav.Settings");
    }

    /// <summary>Handles localization changes by refreshing navigation labels.</summary>
    /// <param name="sender">Localization provider that raised the event. May be null.</param>
    /// <param name="e">Empty event data.</param>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedText();
    }

    /// <summary>Handles restart marker changes by refreshing the settings navigation label.</summary>
    private void OnRestartPendingChanged(object? sender, EventArgs e)
    {
        SettingsText = _restartState.IsRestartPending
            ? $"{_localization.GetString("Nav.Settings")}*"
            : _localization.GetString("Nav.Settings");
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.ViewModel;

/// <summary>Bindable view model for the about page.</summary>
/// <remarks>
/// Invariants: Static labels are available immediately and mihomo status is non-null.
/// Thread safety: Not thread-safe; intended for UI-thread binding.
/// Side effects: Commands can launch external URIs and load can probe the core binary.
/// </remarks>
internal sealed class AboutViewModel : ObservableObject
{
    /// <summary>Clash# repository URL.</summary>
    private static readonly Uri GitHubUri = new("https://github.com/Water-Run/ClashSharp");

    /// <summary>mihomo upstream repository URL.</summary>
    private static readonly Uri MihomoUri = new("https://github.com/MetaCubeX/mihomo");

    /// <summary>Localization provider used by this view model.</summary>
    private readonly IDisplayPageLocalization _localization;

    /// <summary>Core provider used by status loading.</summary>
    private readonly IAboutCore _core;

    /// <summary>Read-only application release checker.</summary>
    private readonly IApplicationUpdateChecker _updateChecker;

    /// <summary>URI launcher used by link commands.</summary>
    private readonly IUriLauncher _launcher;

    /// <summary>Backing field for <see cref="MihomoStatusText"/>.</summary>
    private string _mihomoStatusText = string.Empty;

    /// <summary>Backing field for <see cref="UpdateStatusText"/>.</summary>
    private string _updateStatusText = string.Empty;

    /// <summary>Initializes an about view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="core">Core provider. Must not be null.</param>
    /// <param name="launcher">URI launcher. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public AboutViewModel(
        IDisplayPageLocalization localization,
        IAboutCore core,
        IApplicationUpdateChecker updateChecker,
        IUriLauncher launcher,
        IApplicationErrorSink errorSink)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _updateChecker = updateChecker ?? throw new ArgumentNullException(nameof(updateChecker));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        ArgumentNullException.ThrowIfNull(errorSink);
        MihomoStatusText = _localization.GetString("About.Mihomo.Loading");
        UpdateStatusText = _localization.GetString("About.Update.Checking");
        LoadCommand = new AsyncRelayCommand(
            LoadAsync,
            errorSink,
            operationName: "about-load");
        OpenGitHubCommand = new AsyncRelayCommand(
            (_, token) => _launcher.LaunchAsync(GitHubUri, token),
            errorSink,
            operationName: "about-open-github");
        OpenMihomoCommand = new AsyncRelayCommand(
            (_, token) => _launcher.LaunchAsync(MihomoUri, token),
            errorSink,
            operationName: "about-open-mihomo");
        OpenReleasesCommand = new AsyncRelayCommand(
            (_, token) => _launcher.LaunchAsync(GitHubReleaseUpdateCheckerUri, token),
            errorSink,
            operationName: "about-open-releases");
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _localization.GetString("Nav.About");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _localization.GetString("Page.About.Description");

    /// <summary>Gets the app description text.</summary>
    /// <value>Localized app description.</value>
    public string AppDescriptionText => _localization.GetString("About.App.Description");

    /// <summary>Gets the application name text.</summary>
    /// <value>Application display name.</value>
    public string AppNameText => "Clash#";

    /// <summary>Gets the application version text.</summary>
    /// <value>Application package or assembly version.</value>
    public string VersionText => _updateChecker.CurrentVersion;

    /// <summary>Gets the application version summary text.</summary>
    /// <value>Localized version summary.</value>
    public string VersionSummaryText => string.Format(CultureInfo.CurrentCulture, _localization.GetString("About.Version.Value.Format"), VersionText);

    /// <summary>Gets the version label text.</summary>
    /// <value>Localized version field label.</value>
    public string VersionLabelText => _localization.GetString("About.Version.Title");

    /// <summary>Gets the runtime title text.</summary>
    /// <value>Localized runtime field label.</value>
    public string RuntimeTitleText => _localization.GetString("About.Runtime.Title");

    /// <summary>Gets the runtime value text.</summary>
    /// <value>Runtime stack summary for this application.</value>
    public string RuntimeValueText => _localization.GetString("About.Runtime.Value");

    /// <summary>Gets the author title text.</summary>
    /// <value>Localized author title.</value>
    public string AuthorTitleText => _localization.GetString("About.Author.Title");

    /// <summary>Gets the author value text.</summary>
    /// <value>Localized author value.</value>
    public string AuthorValueText => _localization.GetString("About.Author.Value");

    /// <summary>Gets the open-source title text.</summary>
    /// <value>Localized open-source title.</value>
    public string OpenSourceTitleText => _localization.GetString("About.OpenSource.Title");

    /// <summary>Gets the open-source description text.</summary>
    /// <value>Localized open-source description.</value>
    public string OpenSourceDescriptionText => _localization.GetString("About.OpenSource.Description");

    /// <summary>Gets the protocol title text.</summary>
    /// <value>Localized protocol title.</value>
    public string ProtocolTitleText => _localization.GetString("About.Protocol.Title");

    /// <summary>Gets the protocol value text.</summary>
    /// <value>Localized protocol value.</value>
    public string ProtocolValueText => _localization.GetString("About.Protocol.Value");

    /// <summary>Gets the license title text.</summary>
    /// <value>Localized license field label.</value>
    public string LicenseTitleText => _localization.GetString("About.License.Title");

    /// <summary>Gets the license value text.</summary>
    /// <value>Localized license value.</value>
    public string LicenseValueText => _localization.GetString("About.Protocol.Value");

    /// <summary>Gets the GitHub title text.</summary>
    /// <value>Localized GitHub title.</value>
    public string GitHubTitleText => _localization.GetString("About.GitHub.Title");

    /// <summary>Gets the GitHub description text.</summary>
    /// <value>Localized GitHub description.</value>
    public string GitHubDescriptionText => _localization.GetString("About.GitHub.Description");

    /// <summary>Gets the GitHub button text.</summary>
    /// <value>Localized GitHub button text.</value>
    public string GitHubButtonText => _localization.GetString("About.OpenGitHub");

    /// <summary>Gets the software update title.</summary>
    public string UpdateTitleText => _localization.GetString("About.Update.Title");

    /// <summary>Gets the software update policy description.</summary>
    public string UpdateDescriptionText => _localization.GetString("About.Update.Description");

    /// <summary>Gets the button text for the fixed project releases page.</summary>
    public string OpenReleasesButtonText => _localization.GetString("About.Update.OpenReleases");

    /// <summary>Gets the mihomo title text.</summary>
    /// <value>Localized mihomo title.</value>
    public string MihomoTitleText => _localization.GetString("About.Mihomo.Title");

    /// <summary>Gets the mihomo description text.</summary>
    /// <value>Localized mihomo description.</value>
    public string MihomoDescriptionText => _localization.GetString("About.Mihomo.Description");

    /// <summary>Gets the mihomo button text.</summary>
    /// <value>Localized mihomo button text.</value>
    public string MihomoButtonText => _localization.GetString("About.OpenMihomo");

    /// <summary>Gets the local proxy information title text.</summary>
    /// <value>Localized proxy information title.</value>
    public string ProxyInformationTitleText => _localization.GetString("About.ProxyInformation.Title");

    /// <summary>Gets the local proxy information description text.</summary>
    /// <value>Localized proxy information description.</value>
    public string ProxyInformationDescriptionText => _localization.GetString("About.ProxyInformation.Description");

    /// <summary>Gets the proxy information button text.</summary>
    /// <value>Localized proxy information button text.</value>
    public string ProxyInformationButtonText => _localization.GetString("About.ProxyInformation.Open");

    /// <summary>Gets bundled mihomo status text.</summary>
    /// <value>Status text; never null.</value>
    public string MihomoStatusText
    {
        get => _mihomoStatusText;
        private set => SetProperty(ref _mihomoStatusText, value);
    }

    /// <summary>Gets the current application release availability text.</summary>
    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    /// <summary>Gets the command that loads mihomo status.</summary>
    /// <value>Asynchronous load command.</value>
    public AsyncRelayCommand LoadCommand { get; }

    /// <summary>Gets the command that opens the project repository.</summary>
    /// <value>Asynchronous URI launch command.</value>
    public AsyncRelayCommand OpenGitHubCommand { get; }

    /// <summary>Gets the command that opens the upstream mihomo repository.</summary>
    /// <value>Asynchronous URI launch command.</value>
    public AsyncRelayCommand OpenMihomoCommand { get; }

    /// <summary>Gets the command that opens the fixed project releases page.</summary>
    public AsyncRelayCommand OpenReleasesCommand { get; }

    /// <summary>Loads bundled mihomo version status.</summary>
    /// <param name="cancellationToken">Cancels version probing when requested.</param>
    /// <returns>A task that completes after status text is updated.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the core provider.
    /// Thread / reentrancy: UI callers should use <see cref="LoadCommand"/> to prevent reentrancy.
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            LoadCoreStatusAsync(cancellationToken),
            LoadUpdateStatusAsync(cancellationToken));
    }

    private async Task LoadCoreStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            string versionText = CoreVersionDisplayFormatter.Format(await _core.GetVersionTextAsync(cancellationToken));
            MihomoStatusText = string.Format(CultureInfo.CurrentCulture, _localization.GetString("About.Mihomo.Available.Format"), versionText);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or InvalidOperationException or OperationCanceledException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            MihomoStatusText = _localization.GetString("About.Mihomo.Unavailable");
        }
    }

    private async Task LoadUpdateStatusAsync(CancellationToken cancellationToken)
    {
        ApplicationUpdateCheckResult result;
        try
        {
            result = await _updateChecker.CheckAsync(cancellationToken);
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            result = ApplicationUpdateCheckResult.Unavailable();
        }

        UpdateStatusText = result.Availability switch
        {
            ApplicationUpdateAvailability.Current => _localization.GetString("About.Update.Current"),
            ApplicationUpdateAvailability.UpdateAvailable => string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetString("About.Update.Available.Format"),
                result.LatestVersion),
            _ => _localization.GetString("About.Update.Unavailable"),
        };
    }

    /// <summary>Fixed human-facing releases URI; never obtained from a remote response.</summary>
    private static readonly Uri GitHubReleaseUpdateCheckerUri =
        new("https://github.com/Water-Run/ClashSharp/releases/latest");

}

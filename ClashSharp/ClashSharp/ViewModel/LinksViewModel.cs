using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable view model for subscription link management.</summary>
/// <remarks>
/// Invariants: Link rows are non-null after construction.
/// Thread safety: Not thread-safe; intended for UI-thread binding and command execution.
/// Side effects: Commands can add, check, import subscription links, and write logs.
/// </remarks>
internal sealed class LinksViewModel : ObservableObject
{
    /// <summary>Localization resolver used by visible labels.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Subscription catalog boundary used by link operations.</summary>
    private readonly ISubscriptionLinkCatalog _profiles;

    /// <summary>Presentation log boundary used by link operations.</summary>
    private readonly IPageLog _log;

    /// <summary>Reports unexpected asynchronous presentation failures.</summary>
    private readonly IApplicationErrorSink _errorSink;

    /// <summary>Maps persisted link models to UI-only display rows.</summary>
    private readonly IModelDisplayMapper _displayMapper;

    /// <summary>Backing field for <see cref="SubscriptionLinks"/>.</summary>
    private IReadOnlyList<ProfileSubscriptionLinkDisplay> _subscriptionLinks = [];

    /// <summary>Backing field for <see cref="SelectedLink"/>.</summary>
    private ProfileSubscriptionLinkDisplay? _selectedLink;

    /// <summary>Initializes a links view model.</summary>
    /// <param name="getString">Localization resolver. Must not be null.</param>
    /// <param name="profiles">Profile catalog service. Must not be null.</param>
    /// <param name="log">Log service. Must not be null.</param>
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <param name="displayMapper">UI display row mapper. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public LinksViewModel(
        Func<string, string> getString,
        ISubscriptionLinkCatalog profiles,
        IPageLog log,
        IApplicationErrorSink errorSink,
        IModelDisplayMapper displayMapper)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        _displayMapper = displayMapper ?? throw new ArgumentNullException(nameof(displayMapper));
        AddLinkCommand = new AsyncRelayCommand(
            AddSubscriptionLinkFromCommandAsync,
            _errorSink,
            operationName: "links-add");
        CheckLinkCommand = new AsyncRelayCommand(
            CheckSelectedLinkAsync,
            _errorSink,
            operationName: "links-check");
        UpdateLinkCommand = new AsyncRelayCommand(
            UpdateSelectedLinkAsync,
            _errorSink,
            operationName: "links-update");
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _getString("Nav.Links");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _getString("Page.Links.Description");

    /// <summary>Gets the add command label.</summary>
    /// <value>Localized command label.</value>
    public string AddLinkText => _getString("Command.Add");

    /// <summary>Gets the check command label.</summary>
    /// <value>Localized command label.</value>
    public string CheckLinksText => _getString("Command.Check");

    /// <summary>Gets the update command label.</summary>
    /// <value>Localized command label.</value>
    public string UpdateLinksText => _getString("Command.Update");

    /// <summary>Gets subscription link rows.</summary>
    /// <value>Subscription link rows; never null.</value>
    public IReadOnlyList<ProfileSubscriptionLinkDisplay> SubscriptionLinks
    {
        get => _subscriptionLinks;
        private set => SetProperty(ref _subscriptionLinks, value);
    }

    /// <summary>Gets or sets the selected subscription link.</summary>
    /// <value>Selected link, or null when none is selected.</value>
    public ProfileSubscriptionLinkDisplay? SelectedLink
    {
        get => _selectedLink;
        set => SetProperty(ref _selectedLink, value);
    }

    /// <summary>Gets the command that adds link input accepted by the page.</summary>
    /// <value>Asynchronous add command.</value>
    public AsyncRelayCommand AddLinkCommand { get; }

    /// <summary>Gets the command that checks the selected link.</summary>
    /// <value>Asynchronous check command.</value>
    public AsyncRelayCommand CheckLinkCommand { get; }

    /// <summary>Gets the command that imports the selected link.</summary>
    /// <value>Asynchronous update command.</value>
    public AsyncRelayCommand UpdateLinkCommand { get; }

    /// <summary>Loads subscription links without blocking the UI thread.</summary>
    /// <param name="cancellationToken">Cancels this page-load attempt.</param>
    /// <returns>A task that completes after the snapshot is applied or the failure is reported.</returns>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        return ViewModelLoadExecutor.ExecuteAsync(
            _profiles.GetSubscriptionLinks,
            ApplyLinks,
            _errorSink,
            "links-load",
            cancellationToken);
    }

    /// <summary>Adds a subscription link and refreshes visible rows.</summary>
    /// <param name="name">Link name. Must not be null.</param>
    /// <param name="uri">Subscription URI. Must not be null.</param>
    /// <param name="cancellationToken">Cancels persistence and the following reload.</param>
    /// <returns>A task that completes after add handling finishes.</returns>
    public async Task AddSubscriptionLinkAsync(
        string name,
        string uri,
        CancellationToken cancellationToken)
    {
        try
        {
            ProfileSubscriptionLink link = await _profiles.AddSubscriptionLinkAsync(
                name,
                uri,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _log.Append("Info", "Links", $"Subscription link added: {link.Name}.", link.Uri);
            await LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
                or OperationCanceledException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Links", "Subscription link could not be added.", exception.Message);
        }
    }

    /// <summary>Checks the selected subscription link.</summary>
    /// <param name="cancellationToken">Cancels the check when requested.</param>
    /// <returns>A task that completes after check handling finishes.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the profile catalog service.
    /// Thread / reentrancy: UI callers should use <see cref="CheckLinkCommand"/>.
    /// </remarks>
    public async Task CheckSelectedLinkAsync(CancellationToken cancellationToken)
    {
        if (SelectedLink is not ProfileSubscriptionLinkDisplay selectedLink)
        {
            _log.Append("Info", "Links", "No subscription link selected.", null);
            return;
        }

        ProfileSubscriptionLink link = selectedLink.Model;
        try
        {
            string status = await _profiles.CheckSubscriptionLinkAsync(link, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _log.Append("Info", "Links", $"Subscription link check completed: {status}.", link.Name);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or OperationCanceledException
                or InvalidOperationException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Links", "Subscription link check failed.", exception.Message);
        }

        await LoadAsync(cancellationToken);
    }

    /// <summary>Imports the selected subscription link as a profile.</summary>
    /// <param name="cancellationToken">Cancels the import when requested.</param>
    /// <returns>A task that completes after import handling finishes.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the profile catalog service.
    /// Thread / reentrancy: UI callers should use <see cref="UpdateLinkCommand"/>.
    /// </remarks>
    public async Task UpdateSelectedLinkAsync(CancellationToken cancellationToken)
    {
        if (SelectedLink is not ProfileSubscriptionLinkDisplay selectedLink)
        {
            _log.Append("Info", "Links", "No subscription link selected.", null);
            return;
        }

        ProfileSubscriptionLink link = selectedLink.Model;
        try
        {
            ProfileImportResult result = await _profiles.ImportSubscriptionLinkAsync(link, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _log.Append("Info", "Links", $"Subscription profile imported: {result.ProfileName}.", result.ConfigPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or HttpRequestException
                or OperationCanceledException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Links", "Subscription profile import failed.", exception.Message);
        }

        await LoadAsync(cancellationToken);
    }

    private Task AddSubscriptionLinkFromCommandAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        if (parameter is not ValueTuple<string, string> linkInput)
        {
            throw new ArgumentException(
                "The add-link command requires a name and URI.",
                nameof(parameter));
        }

        return AddSubscriptionLinkAsync(
            linkInput.Item1,
            linkInput.Item2,
            cancellationToken);
    }

    private void ApplyLinks(IReadOnlyList<ProfileSubscriptionLink> links)
    {
        List<ProfileSubscriptionLinkDisplay> rows = new(links.Count);
        foreach (ProfileSubscriptionLink link in links)
        {
            rows.Add(_displayMapper.Map(link));
        }

        SubscriptionLinks = rows;
    }
}

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed partial class ProfilesViewModel
{
    private ISubscriptionLinkCatalog? _subscriptions;
    private string _subscriptionUri = string.Empty;
    private string _subscriptionName = string.Empty;
    private bool _isImporting;
    private string? _lastImportedProfileId;

    /// <summary>Gets the command for downloading and importing a subscription in one operation.</summary>
    public AsyncRelayCommand ImportSubscriptionCommand { get; private set; } = null!;
    /// <summary>Gets or sets the subscription URL draft, retained when importing fails.</summary>
    public string SubscriptionUri { get => _subscriptionUri; set => SetProperty(ref _subscriptionUri, value); }
    /// <summary>Gets or sets the optional display name draft.</summary>
    public string SubscriptionName { get => _subscriptionName; set => SetProperty(ref _subscriptionName, value); }
    /// <summary>Gets whether a subscription import owns the current operation.</summary>
    public bool IsImporting { get => _isImporting; private set { if (SetProperty(ref _isImporting, value)) { OnPropertyChanged(nameof(CanImport)); } } }
    /// <summary>Gets whether another import may start.</summary>
    public bool CanImport => !IsImporting;
    /// <summary>Gets whether the imported, currently selected profile can be explicitly activated.</summary>
    public bool CanUseImportedProfile => _lastImportedProfileId is not null
        && SelectedProfile?.Model.Id == _lastImportedProfileId && _getActiveProfileId() != _lastImportedProfileId;
    /// <summary>Gets the subscription input title.</summary>
    public string ImportSubscriptionText => _getString("Profiles.Import.Subscription");
    /// <summary>Gets the optional name input title.</summary>
    public string ImportNameText => _getString("Profiles.Import.Name");
    /// <summary>Gets the URL placeholder.</summary>
    public string ImportUrlPlaceholderText => _getString("Profiles.Import.Placeholder");
    /// <summary>Gets the import action label.</summary>
    public string ImportActionText => _getString("Command.Import");
    /// <summary>Gets the in-progress import label.</summary>
    public string ImportProgressText => _getString("Profiles.Import.Progress");

    private void InitializeImport(ISubscriptionLinkCatalog? subscriptions)
    {
        _subscriptions = subscriptions;
        ImportSubscriptionCommand = new AsyncRelayCommand(ImportSubscriptionAsync, _errorSink, operationName: "profiles-subscription-import");
    }

    /// <summary>Imports the entered URL without changing the active profile or proxy mode.</summary>
    /// <param name="cancellationToken">Cancels downloading, validation and page refresh.</param>
    public async Task ImportSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (IsImporting || _subscriptions is null) { return; }
        ClearStatus();
        if (!Uri.TryCreate(SubscriptionUri.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            SetStatus("Profiles.Import.InvalidUrl", PageStatusSeverity.Error);
            return;
        }
        IsImporting = true;
        try
        {
            var imported = await SubscriptionImportWorkflow.ImportAsync(_subscriptions, SubscriptionName, SubscriptionUri, cancellationToken);
            await LoadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ShowImportResult(imported.Profile);
            SubscriptionUri = string.Empty;
            SubscriptionName = string.Empty;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Profiles", "Subscription import failed.", exception.GetType().Name);
            SetFailure("Profiles.Status.ImportFailed", exception);
        }
        finally { IsImporting = false; }
    }

    private void ShowImportResult(ProfileImportResult result)
    {
        _lastImportedProfileId = result.ProfileId;
        SelectedProfile = Profiles.FirstOrDefault(row => row.Model.Id == result.ProfileId);
        StatusSeverity = PageStatusSeverity.Success;
        _statusTextFactory = () => string.Format(CultureInfo.CurrentCulture,
            _getString(_getActiveProfileId() == result.ProfileId ? "Profiles.Import.ActiveSuccess.Format" : "Profiles.Import.Success.Format"),
            result.ProfileName, result.NodeCount, result.RuleCount);
        StatusText = _statusTextFactory();
        OnPropertyChanged(nameof(CanUseImportedProfile));
    }

    private void RefreshImportLocalization()
    {
        foreach (string name in new[] { nameof(ImportSubscriptionText), nameof(ImportNameText), nameof(ImportUrlPlaceholderText),
            nameof(ImportActionText), nameof(ImportProgressText) }) { OnPropertyChanged(name); }
    }

    /// <summary>Explains a rejected file drop without opening or modifying the dropped content.</summary>
    public void ReportInvalidDroppedFile() => SetStatus("Profiles.Import.DropInvalid", PageStatusSeverity.Error);
}

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

/// <summary>Shows persisted failures without automatically repeating their effects.</summary>
internal sealed class SettingsRecoveryViewModel(Func<string, string> getString, ISettingsApplicationRecovery recovery,
    IApplicationErrorSink errors, Action requireRestart) : ObservableObject
{
    private bool _isBusy;
    private string _errorText = string.Empty;
    public ObservableCollection<SettingsApplicationIssue> Issues { get; } = [];
    public bool HasIssues => Issues.Count > 0;
    public bool IsVisible => HasIssues || HasError;
    public bool CanRetry => !_isBusy;
    public string Title => getString("Settings.Recovery.Title");
    public string Description => getString("Settings.Recovery.Description");
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public void Refresh()
    {
        if (_isBusy) { return; }
        SettingsAuthoritySnapshot snapshot;
        try { snapshot = recovery.CaptureSnapshot(); }
        catch (DataGenerationManagerException failure) when (failure.Error == DataGenerationManagerError.Draining) { return; }
        Issues.Clear();
        foreach (SettingsApplicationBatch batch in snapshot.Envelope.PendingApplications.Where(batch => batch.State == SettingsApplicationBatchState.Failed))
        {
            Issues.Add(new(getString("Settings.Recovery.Kind." + batch.ApplicationKind), getString("Settings.Reset.Retry"), snapshot, batch.BatchId));
        }
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
    }

    public async Task RetryAsync(SettingsApplicationIssue issue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (_isBusy || !Issues.Contains(issue)) { return; }
        _isBusy = true;
        OnPropertyChanged(nameof(CanRetry));
        ErrorText = string.Empty;
        try
        {
            SettingsApplicationRecoveryResult result = await recovery.RetryApplicationAsync(issue.Snapshot, issue.BatchId, cancellationToken);
            if (result.NotificationFailure is not null || result.Outcome.Status == SettingsAuthorityStatus.DeferredToRestart) { requireRestart(); }
            if (!result.Outcome.IsSucceeded && result.Outcome.Status != SettingsAuthorityStatus.DeferredToRestart)
            {
                ErrorText = getString(result.Outcome.Code == "settings.recovery.stale_attempt" ? "Settings.Recovery.Stale" : "Settings.Recovery.RetryFailed");
            }
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)
            && !ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken))
        {
            ErrorText = getString("Settings.Recovery.RetryFailed");
            await errors.ReportAsync(new ApplicationError("settings-application-retry", failure), CancellationToken.None);
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsVisible));
            Refresh();
        }
    }
}

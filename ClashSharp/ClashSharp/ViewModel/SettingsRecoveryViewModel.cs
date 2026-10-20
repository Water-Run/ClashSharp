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
    private string? _errorResourceKey;
    private DataGenerationDescriptor? _errorGeneration;
    private Guid? _errorBatchId;
    public ObservableCollection<SettingsApplicationIssue> Issues { get; } = [];
    public bool HasIssues => Issues.Count > 0;
    public bool IsVisible => HasIssues || HasError;
    public bool CanRetry => !_isBusy;
    public string Title => getString("Settings.Recovery.Title");
    public string Description => getString("Settings.Recovery.Description");
    public string ErrorText => _errorResourceKey is { } key ? getString(key) : string.Empty;
    public bool HasError => _errorResourceKey is not null;

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
        if (_errorBatchId is Guid failed
            && (_errorGeneration?.IsSameGeneration(snapshot.Generation) != true || !Issues.Any(issue => issue.BatchId == failed)))
        {
            ClearRetryError();
        }
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
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
        ClearRetryError();
        try
        {
            SettingsApplicationRecoveryResult result = await recovery.RetryApplicationAsync(issue.Snapshot, issue.BatchId, cancellationToken);
            if (result.NotificationFailure is not null || result.Outcome.Status == SettingsAuthorityStatus.DeferredToRestart) { requireRestart(); }
            if (!result.Outcome.IsSucceeded && result.Outcome.Status != SettingsAuthorityStatus.DeferredToRestart)
            {
                SetRetryError(issue, result.Outcome.Code == "settings.recovery.stale_attempt" ? "Settings.Recovery.Stale" : "Settings.Recovery.RetryFailed");
            }
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)
            && !ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken))
        {
            SetRetryError(issue, "Settings.Recovery.RetryFailed");
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

    private void SetRetryError(SettingsApplicationIssue issue, string resourceKey)
    {
        _errorResourceKey = resourceKey;
        _errorGeneration = issue.Snapshot.Generation;
        _errorBatchId = issue.BatchId;
        NotifyErrorState();
    }

    private void ClearRetryError()
    {
        _errorResourceKey = null;
        _errorGeneration = null;
        _errorBatchId = null;
        NotifyErrorState();
    }

    private void NotifyErrorState()
    {
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsVisible));
    }
}

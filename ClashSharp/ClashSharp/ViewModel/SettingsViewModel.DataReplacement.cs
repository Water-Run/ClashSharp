using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.ViewModel;

internal sealed partial class SettingsViewModel
{
    private readonly Func<CancellationToken, Task<SettingsDataReplacementResult>>? _replaceAllSettingsAsync;
    private bool _dataReplacementRestartPending;
    private bool _dataReplacementHasWarnings;

    public bool HasDataReplacementWarning => _dataReplacementHasWarnings && !_dataReplacementRestartPending;
    public string DataReplacementWarningText => _getString("Settings.DataReplacement.Warning.Description");

    private async Task ResetThroughDataReplacementAsync(CancellationToken cancellationToken)
    {
        RestartRequiredSettingsBaseline baseline = CaptureRestartRequiredSettingsBaseline();
        SettingsDataReplacementResult result;
        try { result = await _replaceAllSettingsAsync!(cancellationToken); }
        catch (SettingsDataReplacementRecoveryException)
        {
            // The replacement port has already requested startup recovery. Do not issue another restart
            // or reload repositories while the owning transaction still has admission closed.
            IsResetRecoveryRequired = true;
            _dataReplacementRestartPending = true;
            OperationErrorText = _getString("Settings.DataReplacement.RecoveryRequired.Description");
            OnPropertyChanged(nameof(HasRestartRequiredSettings));
            throw;
        }

        _dataReplacementRestartPending |= result.RequiresRestart;
        ClearRuntimeGroupResetRetry();
        _dataReplacementHasWarnings = result.Warnings.Count > 0;
        OperationErrorText = string.Empty;
        ReloadAfterSettingsReset(baseline);
        OnPropertyChanged(nameof(HasRestartRequiredSettings));
        OnPropertyChanged(nameof(HasDataReplacementWarning));
        OnPropertyChanged(nameof(DataReplacementWarningText));
    }
}

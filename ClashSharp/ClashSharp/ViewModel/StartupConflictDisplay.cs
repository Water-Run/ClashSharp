using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Displays one captured conflict and repair outcome without running detection or repair during translation.</summary>
internal sealed class StartupConflictDisplay(StartupConflictIssue issue, Func<string, string> getString) : ObservableObject
{
    private bool _running;
    private StartupConflictRepairResult? _result;
    public StartupConflictIssue Issue { get; } = issue;
    public string Title => Issue.TitleSource?.Resolve(getString) ?? Issue.Title;
    public string Description => Issue.DescriptionSource?.Resolve(getString) ?? Issue.Description;
    public string RepairText => Issue.RepairTextSource?.Resolve(getString) ?? Issue.RepairText;
    public bool IsRepairRunning => _running;
    public bool CanRepair => Issue.HasRepairAction && !_running && _result?.Succeeded != true;
    public string StatusText
    {
        get
        {
            if (_running) { return getString("StartupConflict.Status.Fixing"); }
            if (_result is StartupConflictRepairResult result)
            {
                return result.MessageSource?.Resolve(getString)
                    ?? (string.IsNullOrWhiteSpace(result.Message)
                        ? getString(result.Succeeded ? "StartupConflict.Status.Succeeded" : "StartupConflict.Status.Failed") : result.Message);
            }
            string text = getString(Issue.HasRepairAction ? "StartupConflict.Status.Ready" : "StartupConflict.Status.ManualReview");
            return RuntimeFailureDiagnostics.IsStableCode(Issue.DiagnosticCode) ? $"{text} [{Issue.DiagnosticCode}]" : text;
        }
    }

    public bool BeginRepair()
    {
        if (!CanRepair) { return false; }
        _running = true;
        NotifyState();
        return true;
    }

    public void CompleteRepair(StartupConflictRepairResult result)
    {
        _result = result;
        _running = false;
        NotifyState();
    }

    public void EndRepair()
    {
        _running = false;
        NotifyState();
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(RepairText));
        OnPropertyChanged(nameof(StatusText));
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRepairRunning));
        OnPropertyChanged(nameof(CanRepair));
    }
}

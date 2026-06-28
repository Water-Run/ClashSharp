/*
 * Settings Diagnostics ViewModel
 * Routes Windows-native diagnostic commands without depending on WinUI controls
 *
 * @author: WaterRun
 * @file: ViewModel/SettingsDiagnosticsViewModel.cs
 * @date: 2026-06-17
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Runs Windows-native diagnostic operations for the settings diagnostics view model.</summary>
internal interface IWindowsDiagnosticsClient
{
    Task<WindowsDiagnosticResult> DiagnoseAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken);

    Task<WindowsDiagnosticResult> ApplyAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken);

    Task<WindowsDiagnosticResult> ResetAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken);
}

/// <summary>Writes diagnostic command logs for the settings diagnostics view model.</summary>
internal interface IDiagnosticsLog
{
    void Append(string level, string category, string message, string? detail);
}

/// <summary>Status update returned to the settings page after a diagnostic command.</summary>
/// <param name="Target">Diagnostic target whose visible status should be updated.</param>
/// <param name="Message">Status message to display.</param>
internal readonly record struct SettingsDiagnosticStatus(WindowsDiagnosticTarget Target, string Message);

/// <summary>Owns Windows-native diagnostic command parsing, execution, logging, and failure messages.</summary>
/// <remarks>
/// Invariants: Valid command tags use the form "Target:Action" where action is Diagnose, Apply, or Reset.
/// Thread safety: Not thread-safe; intended for UI-triggered command execution.
/// Side effects: Executes injected diagnostic operations and writes injected logs.
/// </remarks>
internal sealed class SettingsDiagnosticsViewModel
{
    /// <summary>Log category used for all Windows diagnostic command logs.</summary>
    private const string LogCategory = "WindowsDiagnostics";

    /// <summary>Diagnostic command action for status-only checks.</summary>
    private const string DiagnosticActionDiagnose = "Diagnose";

    /// <summary>Diagnostic command action for applying repair settings.</summary>
    private const string DiagnosticActionApply = "Apply";

    /// <summary>Diagnostic command action for resetting repair settings.</summary>
    private const string DiagnosticActionReset = "Reset";

    /// <summary>Diagnostic runner used by this view model.</summary>
    private readonly IWindowsDiagnosticsClient _diagnostics;

    /// <summary>Diagnostic log writer used by this view model.</summary>
    private readonly IDiagnosticsLog _log;

    /// <summary>Localization lookup used for user-facing failure messages.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Initializes a new diagnostics view model.</summary>
    /// <param name="diagnostics">Diagnostic operation runner. Must not be null.</param>
    /// <param name="log">Diagnostic log writer. Must not be null.</param>
    /// <param name="getString">Localization lookup. Null returns localization keys directly.</param>
    public SettingsDiagnosticsViewModel(IWindowsDiagnosticsClient diagnostics, IDiagnosticsLog log, Func<string, string>? getString = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _getString = getString ?? (key => key);
    }

    /// <summary>Executes a diagnostic command tag and returns the status that should be shown by the page.</summary>
    /// <param name="commandTag">Command tag in the form "Target:Action".</param>
    /// <param name="cancellationToken">Cancels diagnostic work.</param>
    /// <returns>A status update for valid commands; otherwise null.</returns>
    public async Task<SettingsDiagnosticStatus?> ExecuteCommandAsync(string? commandTag, CancellationToken cancellationToken)
    {
        if (!TryParseCommand(commandTag, out WindowsDiagnosticTarget target, out string action))
        {
            _log.Append("Warning", LogCategory, _getString("Diagnostic.UnsupportedCommand"), commandTag);
            return null;
        }

        try
        {
            WindowsDiagnosticResult result = action switch
            {
                DiagnosticActionDiagnose => await _diagnostics.DiagnoseAsync(target, cancellationToken).ConfigureAwait(false),
                DiagnosticActionApply => await _diagnostics.ApplyAsync(target, cancellationToken).ConfigureAwait(false),
                DiagnosticActionReset => await _diagnostics.ResetAsync(target, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported diagnostic action."),
            };

            _log.Append("Info", LogCategory, result.Message, result.Detail);
            return new SettingsDiagnosticStatus(result.Target, result.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            string message = GetFailureMessage(action);
            _log.Append("Warning", LogCategory, message, exception.Message);
            return new SettingsDiagnosticStatus(target, message);
        }
    }

    /// <summary>Parses one command tag into a diagnostic target and action.</summary>
    /// <param name="commandTag">Command tag. May be null.</param>
    /// <param name="target">Parsed diagnostic target.</param>
    /// <param name="action">Parsed action. Empty on failure.</param>
    /// <returns>True when the tag contains a supported target and action.</returns>
    private static bool TryParseCommand(string? commandTag, out WindowsDiagnosticTarget target, out string action)
    {
        target = default;
        action = string.Empty;

        if (string.IsNullOrWhiteSpace(commandTag))
        {
            return false;
        }

        string[] parts = commandTag.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        action = parts[1];
        return TryParseDiagnosticTarget(parts[0], out target)
            && IsSupportedAction(action);
    }

    /// <summary>Returns whether a command action is supported.</summary>
    /// <param name="action">Action text. Must not be null.</param>
    /// <returns>True when the action maps to a known diagnostic operation.</returns>
    private static bool IsSupportedAction(string action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return StringComparer.Ordinal.Equals(action, DiagnosticActionDiagnose)
            || StringComparer.Ordinal.Equals(action, DiagnosticActionApply)
            || StringComparer.Ordinal.Equals(action, DiagnosticActionReset);
    }

    /// <summary>Parses a diagnostic target name from command metadata.</summary>
    /// <param name="value">Target name. Must not be null.</param>
    /// <param name="target">Parsed diagnostic target.</param>
    /// <returns>True when <paramref name="value"/> maps to a known diagnostic target.</returns>
    private static bool TryParseDiagnosticTarget(string value, out WindowsDiagnosticTarget target)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (value)
        {
            case nameof(WindowsDiagnosticTarget.Wsl):
                target = WindowsDiagnosticTarget.Wsl;
                return true;
            case nameof(WindowsDiagnosticTarget.Terminal):
                target = WindowsDiagnosticTarget.Terminal;
                return true;
            case nameof(WindowsDiagnosticTarget.MicrosoftStore):
                target = WindowsDiagnosticTarget.MicrosoftStore;
                return true;
            default:
                target = WindowsDiagnosticTarget.Wsl;
                return false;
        }
    }

    /// <summary>Returns the user-facing failure message for a diagnostic action.</summary>
    /// <param name="action">Diagnostic action.</param>
    /// <returns>Failure status text.</returns>
    private string GetFailureMessage(string action)
    {
        return action switch
        {
            DiagnosticActionApply => _getString("Diagnostic.Failed.Apply"),
            DiagnosticActionReset => _getString("Diagnostic.Failed.Reset"),
            _ => _getString("Diagnostic.Failed.Diagnose"),
        };
    }
}

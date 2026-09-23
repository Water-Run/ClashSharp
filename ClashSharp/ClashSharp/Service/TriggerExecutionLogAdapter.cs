using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Triggers;

namespace ClashSharp.Service;

/// <summary>Publishes trigger outcomes in the existing searchable log without exposing action parameters.</summary>
internal sealed class TriggerExecutionLogAdapter(
    ITriggerDefinitionStore definitions,
    Func<string, string> getString,
    Action<string, string, string, string?> appendLog,
    IApplicationErrorSink errorSink) : ITriggerExecutionLog
{
    private readonly ITriggerDefinitionStore _definitions = definitions
        ?? throw new ArgumentNullException(nameof(definitions));
    private readonly Func<string, string> _getString = getString
        ?? throw new ArgumentNullException(nameof(getString));
    private readonly Action<string, string, string, string?> _appendLog = appendLog
        ?? throw new ArgumentNullException(nameof(appendLog));
    private readonly IApplicationErrorSink _errorSink = errorSink
        ?? throw new ArgumentNullException(nameof(errorSink));

    public void Write(TriggerExecution execution, TriggerActionResult? result)
    {
        ArgumentNullException.ThrowIfNull(execution);
        string name = _definitions.Current.Tasks.FirstOrDefault(task =>
            StringComparer.Ordinal.Equals(task.Definition.Id, execution.TaskId))?.Definition.Name
            ?? execution.TaskId;
        string key = result?.FinalState switch
        {
            TriggerOutboxState.Succeeded => "Triggers.Log.ActionSucceeded.Format",
            TriggerOutboxState.Failed => "Triggers.Log.ActionFailed.Format",
            TriggerOutboxState.Uncertain => "Triggers.Log.ActionUncertain.Format",
            TriggerOutboxState.HandedOff => "Triggers.Log.ActionHandedOff.Format",
            _ => "Triggers.Log.Fired.Format",
        };
        string subject = result is null ? name : string.Format(
            CultureInfo.CurrentCulture,
            "{0} · {1}. {2}",
            name,
            result.Action.ActionIndex + 1,
            _getString($"Triggers.Action.{result.Action.DesiredEffect.Kind}"));
        string level = result?.FinalState switch
        {
            TriggerOutboxState.Failed => "Error",
            TriggerOutboxState.Uncertain => "Warning",
            _ => "Info",
        };
        string details = string.Create(CultureInfo.InvariantCulture,
            $"execution={execution.ExecutionId:N}; revision={execution.TaskRevision}");
        if (result is not null)
        {
            details += string.Create(CultureInfo.InvariantCulture,
                $"; action={result.Action.ActionIndex}; state={result.FinalState}; diagnostic={result.DiagnosticCode}");
        }
        _appendLog(level, "Trigger", string.Format(CultureInfo.CurrentCulture, _getString(key), subject), details);
    }

    public Task ReportFailureAsync(Exception exception) => _errorSink.ReportAsync(
        new ApplicationError("trigger-execution-log", exception), CancellationToken.None);
}

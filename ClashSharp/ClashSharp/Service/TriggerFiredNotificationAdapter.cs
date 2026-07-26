using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Triggers;

namespace ClashSharp.Service;

/// <summary>Resolves committed executions to user-facing names and notification policy.</summary>
internal sealed class TriggerFiredNotificationAdapter : ITriggerFiredNotificationSink
{
    private readonly Func<bool> _getEnabled;
    private readonly ITriggerDefinitionStore _definitions;
    private readonly Func<string, string, bool, CancellationToken, Task> _deliverAsync;
    private readonly Action<string, Exception> _reportFailure;

    public TriggerFiredNotificationAdapter(
        Func<bool> getEnabled,
        ITriggerDefinitionStore definitions,
        Func<string, string, bool, CancellationToken, Task> deliverAsync,
        Action<string, Exception> reportFailure)
    {
        _getEnabled = getEnabled ?? throw new ArgumentNullException(nameof(getEnabled));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _deliverAsync = deliverAsync ?? throw new ArgumentNullException(nameof(deliverAsync));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    public Task NotifyAsync(
        TriggerExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();

        string notificationKey = string.Create(
            CultureInfo.InvariantCulture,
            $"trigger-fired:{execution.ExecutionId:N}:{execution.TaskRevision}");

        return _deliverAsync(
            notificationKey,
            ResolveTriggerName(execution),
            _getEnabled(),
            cancellationToken);
    }

    public void ReportFailure(TriggerExecution execution, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(exception);
        _reportFailure(ResolveTriggerName(execution), exception);
    }

    private string ResolveTriggerName(TriggerExecution execution)
    {
        return _definitions.Current.Tasks
            .FirstOrDefault(task => StringComparer.Ordinal.Equals(
                task.Definition.Id,
                execution.TaskId))
            ?.Definition.Name
            ?? execution.TaskId;
    }
}

extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using ClashSharp.Tests.Unit.ViewModel;
using TriggerExecutionLogAdapter = ClashSharpUi::ClashSharp.Service.TriggerExecutionLogAdapter;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies user-visible execution diagnostics never include action parameters.</summary>
public sealed class TriggerExecutionLogAdapterTests
{
    [Theory]
    [InlineData(TriggerOutboxState.Succeeded, "Info", "ActionSucceeded", null)]
    [InlineData(TriggerOutboxState.Failed, "Error", "ActionFailed", "trigger.action.denied")]
    [InlineData(TriggerOutboxState.Uncertain, "Warning", "ActionUncertain", "trigger.action.probe_unavailable")]
    [InlineData(TriggerOutboxState.HandedOff, "Info", "ActionHandedOff", null)]
    public void Write_UsesLocalizedOutcomeAndSafeExecutionIdentity(
        TriggerOutboxState state, string level, string messageKey, string? diagnostic)
    {
        List<(string Level, string Source, string Message, string? Detail)> records = [];
        TriggerExecutionLogAdapter adapter = new(new EmptyDefinitionStore(), key => key + " {0}",
            (severity, source, message, detail) => records.Add((severity, source, message, detail)),
            new TestApplicationErrorSink());
        TriggerExecution execution = new(Guid.NewGuid(), "removed-task", 3,
            DateTimeOffset.UnixEpoch, Guid.NewGuid(), TriggerExecutionState.Pending);
        TriggerOutboxAction action = new(execution.ExecutionId, 3, 0,
            TriggerIdempotencyKey.Create(execution.ExecutionId, 3, 0),
            new TriggerAction(TriggerActionKind.SendNotification,
                new NotificationActionParameters("private notification text")), state, 1, diagnostic);

        adapter.Write(execution, null);
        adapter.Write(execution, new TriggerActionResult(action, state, diagnostic));

        Assert.Contains("Triggers.Log.Fired.Format removed-task", records[0].Message);
        Assert.Equal(level, records[1].Level);
        Assert.All(records, record =>
        {
            Assert.Equal("Trigger", record.Source);
            Assert.Contains(execution.ExecutionId.ToString("N"), record.Detail);
            Assert.DoesNotContain("private notification text", record.Message + record.Detail);
        });
        Assert.Contains($"Triggers.Log.{messageKey}.Format removed-task", records[1].Message);
        Assert.Contains("Triggers.Action.SendNotification", records[1].Message);
        Assert.Contains($"state={state}", records[1].Detail);
    }

    private sealed class EmptyDefinitionStore : ITriggerDefinitionStore
    {
        public TriggerDefinitionCatalog Current { get; } = new(0, [], []);
        public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReplaceAsync(long expectedGeneration,
            IReadOnlyList<TriggerTaskDefinition> definitions, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using TriggerAction = ClashSharp.Model.Triggers.TriggerAction;
using TriggerFiredNotificationAdapter =
    ClashSharpUi::ClashSharp.Service.TriggerFiredNotificationAdapter;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies durable execution notifications use stable identity and current presentation policy.</summary>
public sealed class TriggerFiredNotificationAdapterTests
{
    [Fact]
    public async Task NotifyAsync_UsesStableExecutionIdentityCachedNameAndCurrentPolicy()
    {
        TriggerTaskDefinition definition = new(
            "task",
            3,
            "Alpha",
            true,
            [
                new TriggerCondition(
                    "condition",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.CloseConnections,
                    new NoActionParameters()),
            ]);
        StaticDefinitionStore definitions = new(new TriggerDefinitionCatalog(
            1,
            [new TriggerDefinitionCatalogItem(definition, null)],
            []));
        string? deliveredKey = null;
        string? deliveredName = null;
        bool? deliveredPolicy = null;
        string? reportedName = null;
        Exception? reportedException = null;
        TriggerFiredNotificationAdapter adapter = new(
            () => false,
            definitions,
            (key, name, enabled, _) =>
            {
                deliveredKey = key;
                deliveredName = name;
                deliveredPolicy = enabled;
                return Task.CompletedTask;
            },
            (name, exception) =>
            {
                reportedName = name;
                reportedException = exception;
            });
        TriggerExecution execution = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            definition.Id,
            definition.Revision,
            DateTimeOffset.UnixEpoch,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TriggerExecutionState.Pending);

        await adapter.NotifyAsync(execution, CancellationToken.None);
        InvalidOperationException failure = new("notification unavailable");
        adapter.ReportFailure(execution, failure);

        Assert.Equal(
            "trigger-fired:11111111111111111111111111111111:3",
            deliveredKey);
        Assert.Equal("Alpha", deliveredName);
        Assert.False(deliveredPolicy);
        Assert.Equal("Alpha", reportedName);
        Assert.Same(failure, reportedException);
    }

    private sealed class StaticDefinitionStore(TriggerDefinitionCatalog current)
        : ITriggerDefinitionStore
    {
        public TriggerDefinitionCatalog Current { get; } = current;

        public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReadAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReplaceAsync(
            long expectedGeneration,
            IReadOnlyList<TriggerTaskDefinition> definitions,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Lifecycle;
using Lifecycle = ClashSharpUi::ClashSharp.Service.ApplicationLifecycleService;

namespace ClashSharp.Tests.Integration;

public sealed partial class GenerationSettingsAuthorityTests
{
    [Fact]
    public async Task GenerationRemoval_PageActionHandsTheExactHostOperationToTheLifetimeWithoutDeletingInline()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RemovalActionFactory factory = new();
        RemovalRequestSink sink = new();
        var actions = CreateActions(fixture, new ActionObserver(fixture), removal: factory, lifecycle: new Lifecycle(sink));

        await actions.ClearAllDataAndRestartAsync(CancellationToken.None);

        Assert.Equal(1, factory.Creations);
        Assert.NotNull(sink.Request);
        Assert.Same(factory.Operation, sink.Request.Maintenance);
        Assert.Equal(0, factory.Operation.Phases);
    }

    [Fact]
    public async Task GenerationRemoval_PageCancellationDoesNotConstructOrSubmitAMaintenanceOperation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RemovalActionFactory factory = new();
        RemovalRequestSink sink = new();
        var actions = CreateActions(fixture, new ActionObserver(fixture), removal: factory, lifecycle: new Lifecycle(sink));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ClearAllDataAndRestartAsync(cancellation.Token));

        Assert.Equal(0, factory.Creations);
        Assert.Null(sink.Request);
        Assert.Equal(0, factory.Operation.Phases);
    }

    [Fact]
    public async Task GenerationRemoval_RejectedLifetimeRequestCannotStartDeletion()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RemovalActionFactory factory = new();
        RemovalRequestSink sink = new() { Reject = true };
        var actions = CreateActions(fixture, new ActionObserver(fixture), removal: factory, lifecycle: new Lifecycle(sink));

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ClearAllDataAndRestartAsync(CancellationToken.None));

        Assert.Equal(1, factory.Creations);
        Assert.Equal(0, factory.Operation.Phases);
    }

    private sealed class RemovalActionFactory : IApplicationDataClearOperationFactory
    {
        public int Creations { get; private set; }
        public RemovalActionOperation Operation { get; } = new();
        public IApplicationLifetimeMaintenance Create() { Creations++; return Operation; }
    }

    private sealed class RemovalActionOperation : IApplicationLifetimeMaintenance
    {
        public int Phases { get; private set; }
        public Task PrepareShutdownAsync(CancellationToken cancellationToken) { Phases++; return Task.CompletedTask; }
        public Task ClearHostDataAsync(CancellationToken cancellationToken) { Phases++; return Task.CompletedTask; }
        public Task ClearLocalFilesAsync(CancellationToken cancellationToken) { Phases++; return Task.CompletedTask; }
    }

    private sealed class RemovalRequestSink : IApplicationLifetimeRequestSink
    {
        public bool Reject { get; init; }
        public ApplicationLifetimeRequest? Request { get; private set; }
        public bool TryRequest(ApplicationLifetimeRequest request) { Request = request; return !Reject; }
    }
}

using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Tests.Unit.Startup;

public sealed class InstallationPinningCoordinatorTests
{
    [Fact]
    public async Task BackgroundWindow_DoesNotReadOrConsumeRequest()
    {
        FakeOperations operations = new() { Foreground = false };
        Assert.Equal(InstallationPinningStatus.Deferred, await new InstallationPinningCoordinator().CompleteAsync(operations, default));
        Assert.Equal(0, operations.ReadCalls);
        Assert.Equal(0, operations.ConsumeCalls);
        Assert.Equal(0, operations.RequestCalls);
    }

    [Fact]
    public async Task NoRequest_DoesNotQueryWindows()
    {
        FakeOperations operations = new() { Pending = null };
        Assert.Equal(InstallationPinningStatus.NoRequest, await new InstallationPinningCoordinator().CompleteAsync(operations, default));
        Assert.Equal(0, operations.QueryCalls);
    }

    [Fact]
    public async Task CancelledBeforeEntry_DoesNotReadRequest()
    {
        FakeOperations operations = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InstallationPinningCoordinator().CompleteAsync(operations, new CancellationToken(true)));
        Assert.Equal(0, operations.ReadCalls);
        Assert.NotNull(operations.Pending);
    }

    [Fact]
    public async Task ForegroundLostDuringQuery_PreservesRequestForNextActivation()
    {
        InstallationPinningCoordinator coordinator = new();
        FakeOperations operations = new();
        operations.Observe = _ =>
        {
            operations.Foreground = false;
            return Task.FromResult(InstallationPinningStatus.Ready);
        };
        Assert.Equal(InstallationPinningStatus.Deferred, await coordinator.CompleteAsync(operations, default));
        Assert.NotNull(operations.Pending);
        Assert.Equal(0, operations.ConsumeCalls);
        operations.Foreground = true;
        operations.Observe = null;
        Assert.Equal(InstallationPinningStatus.Pinned, await coordinator.CompleteAsync(operations, default));
        Assert.Equal(1, operations.RequestCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeConsumption_PreservesRequestAndReleasesAttempt(bool cancelOnForegroundCheck)
    {
        using CancellationTokenSource cancellation = new();
        FakeOperations operations = new();
        if (cancelOnForegroundCheck)
        {
            operations.ObserveForeground = () =>
            {
                if (operations.QueryCalls > 0) { cancellation.Cancel(); }
                return true;
            };
        }
        else
        {
            operations.Observe = _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(InstallationPinningStatus.Ready);
            };
        }
        InstallationPinningCoordinator coordinator = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.CompleteAsync(operations, cancellation.Token));
        Assert.NotNull(operations.Pending);
        Assert.Equal(0, operations.ConsumeCalls);
        Assert.Equal(0, operations.RequestCalls);
        operations.Observe = null;
        operations.ObserveForeground = null;
        Assert.Equal(InstallationPinningStatus.Pinned, await coordinator.CompleteAsync(operations, default));
    }

    [Theory]
    [InlineData(InstallationPinningStatus.AlreadyPinned)]
    [InlineData(InstallationPinningStatus.Unavailable)]
    public async Task TerminalAvailability_ConsumesOnlyOnceWithoutRequesting(InstallationPinningStatus status)
    {
        FakeOperations operations = new() { QueryStatus = status };
        InstallationPinningCoordinator coordinator = new();
        Assert.Equal(status, await coordinator.CompleteAsync(operations, default));
        Assert.Equal(InstallationPinningStatus.NoRequest, await coordinator.CompleteAsync(operations, default));
        Assert.Equal(1, operations.ConsumeCalls);
        Assert.Equal(0, operations.RequestCalls);
    }

    [Theory]
    [InlineData(InstallationPinningStatus.Pinned)]
    [InlineData(InstallationPinningStatus.NotPinned)]
    [InlineData(InstallationPinningStatus.Unavailable)]
    public async Task NativeResult_IsReturnedWithoutRepeatedPrompts(InstallationPinningStatus result)
    {
        FakeOperations operations = new() { RequestResult = result };
        InstallationPinningCoordinator coordinator = new();
        Assert.Equal(result, await coordinator.CompleteAsync(operations, default));
        Assert.Equal(InstallationPinningStatus.NoRequest, await coordinator.CompleteAsync(operations, default));
        Assert.Equal(1, operations.ConsumeCalls);
        Assert.Equal(1, operations.RequestCalls);
    }

    [Fact]
    public async Task ReplacedRequest_IsNotConsumedOrOfferedUnderOldIdentity()
    {
        FakeOperations operations = new();
        operations.Observe = _ =>
        {
            operations.Pending = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            return Task.FromResult(InstallationPinningStatus.Ready);
        };
        InstallationPinningCoordinator coordinator = new();
        Assert.Equal(InstallationPinningStatus.NoRequest, await coordinator.CompleteAsync(operations, default));
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", operations.Pending);
        Assert.Equal(0, operations.RequestCalls);
        operations.Observe = null;
        Assert.Equal(InstallationPinningStatus.Pinned, await coordinator.CompleteAsync(operations, default));
    }

    [Fact]
    public async Task CancellationAfterConsumption_StillSubmitsOnceAndDoesNotRetry()
    {
        using CancellationTokenSource cancellation = new();
        FakeOperations operations = new() { Consumed = cancellation.Cancel };
        operations.Request = token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(InstallationPinningStatus.Pinned);
        };
        InstallationPinningCoordinator coordinator = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.CompleteAsync(operations, cancellation.Token));
        Assert.Equal(1, operations.RequestCalls);
        Assert.Null(operations.Pending);
        Assert.Equal(InstallationPinningStatus.NoRequest, await coordinator.CompleteAsync(operations, default));
        Assert.Equal(1, operations.RequestCalls);
    }

    [Fact]
    public async Task ReentrantActivation_DefersWithoutASecondObservationOrPrompt()
    {
        TaskCompletionSource<InstallationPinningStatus> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeOperations operations = new() { Observe = _ => observed.Task };
        InstallationPinningCoordinator coordinator = new();
        Task<InstallationPinningStatus> first = coordinator.CompleteAsync(operations, default);
        Assert.Equal(InstallationPinningStatus.Deferred, await coordinator.CompleteAsync(operations, default));
        Assert.Equal(1, operations.QueryCalls);
        observed.SetResult(InstallationPinningStatus.Ready);
        Assert.Equal(InstallationPinningStatus.Pinned, await first);
        Assert.Equal(1, operations.RequestCalls);
    }

    [Fact]
    public async Task UnexpectedObservationFailure_PropagatesWithoutConsumingAndCanRetry()
    {
        FakeOperations operations = new() { Observe = _ => throw new InvalidOperationException("observation") };
        InstallationPinningCoordinator coordinator = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CompleteAsync(operations, default));
        Assert.NotNull(operations.Pending);
        operations.Observe = null;
        Assert.Equal(InstallationPinningStatus.Pinned, await coordinator.CompleteAsync(operations, default));
    }

    [Fact]
    public async Task UnexpectedNativeFailure_PropagatesWithoutReofferingConfirmation()
    {
        FakeOperations operations = new() { Request = _ => throw new InvalidOperationException("native") };
        InstallationPinningCoordinator coordinator = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CompleteAsync(operations, default));
        Assert.Null(operations.Pending);
        Assert.Equal(InstallationPinningStatus.NoRequest, await coordinator.CompleteAsync(operations, default));
        Assert.Equal(1, operations.RequestCalls);
    }

    [Fact]
    public async Task InvalidAvailability_DoesNotConsumeRequest()
    {
        FakeOperations operations = new() { QueryStatus = InstallationPinningStatus.Pinned };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstallationPinningCoordinator().CompleteAsync(operations, default));
        Assert.NotNull(operations.Pending);
        Assert.Equal(0, operations.RequestCalls);
    }

    private sealed class FakeOperations : IInstallationPinningOperations
    {
        public bool Foreground { get; set; } = true;
        public string? Pending { get; set; } = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public InstallationPinningStatus QueryStatus { get; init; } = InstallationPinningStatus.Ready;
        public InstallationPinningStatus RequestResult { get; init; } = InstallationPinningStatus.Pinned;
        public Func<bool>? ObserveForeground { get; set; }
        public Func<CancellationToken, Task<InstallationPinningStatus>>? Observe { get; set; }
        public Func<CancellationToken, Task<InstallationPinningStatus>>? Request { get; set; }
        public Action? Consumed { get; init; }
        public int ReadCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public int ConsumeCalls { get; private set; }
        public int RequestCalls { get; private set; }
        public bool IsForeground => ObserveForeground?.Invoke() ?? Foreground;

        public string? ReadPendingRequest()
        {
            ReadCalls++;
            return Pending;
        }

        public bool TryConsumeRequest(string requestId)
        {
            if (Pending != requestId) { return false; }
            ConsumeCalls++;
            Pending = null;
            Consumed?.Invoke();
            return true;
        }

        public Task<InstallationPinningStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            QueryCalls++;
            return Observe?.Invoke(cancellationToken) ?? Task.FromResult(QueryStatus);
        }

        public Task<InstallationPinningStatus> RequestPinAsync(CancellationToken cancellationToken)
        {
            Assert.Null(Pending);
            RequestCalls++;
            return Request?.Invoke(cancellationToken) ?? Task.FromResult(RequestResult);
        }
    }
}

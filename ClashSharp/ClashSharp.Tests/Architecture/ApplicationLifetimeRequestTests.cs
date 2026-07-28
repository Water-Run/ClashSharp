using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace ClashSharp.Tests.Architecture;

/// <summary>Verifies worker-originated lifetime requests hand off before host shutdown begins.</summary>
public sealed class ApplicationLifetimeRequestTests
{
    [Fact]
    public void RequestsWithSameValues_HaveDistinctLifecycleIdentity()
    {
        ApplicationLifetimeRequest first = ApplicationLifetimeRequest.Exit("system-tray");
        ApplicationLifetimeRequest second = ApplicationLifetimeRequest.Exit("system-tray");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task WorkerRequest_ReturnsBeforeOuterRunnerStopsAndDisposesHost()
    {
        List<string> trace = [];
        ApplicationLifetimeRequestChannel channel = new();
        ProcessLifetimeRunner lifetime = new();
        FakeHost host = new(trace);
        lifetime.AttachHost(host);

        Task worker = Task.Run(() =>
        {
            Assert.True(channel.TryRequest(ApplicationLifetimeRequest.Exit("trigger-action")));
            trace.Add("worker-returned");
        });
        ApplicationLifetimeRequest request = await channel.ReadAsync(CancellationToken.None);
        await worker;

        Assert.Equal(ApplicationLifetimeRequestKind.Exit, request.Kind);
        await lifetime.StopAsync(CancellationToken.None);

        Assert.Equal(
            ["worker-returned", "host-stop-enter", "host-stop-unwound", "host-dispose"],
            trace);
    }

    [Fact]
    public async Task ConcurrentRequests_CollapseToOneProcessLifetimeRequest()
    {
        ApplicationLifetimeRequestChannel channel = new();

        bool[] accepted = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(index => Task.Run(() => channel.TryRequest(
                index == 0
                    ? ApplicationLifetimeRequest.Restart("settings")
                    : ApplicationLifetimeRequest.Exit($"request-{index}")))));
        ApplicationLifetimeRequest request = await channel.ReadAsync(CancellationToken.None);

        Assert.Single(accepted, static value => value);
        Assert.NotNull(request);
    }

    [Fact]
    public async Task DuplicateDurableHandoff_IsIdempotentlyAcceptedWhileDifferentRequestLoses()
    {
        ApplicationLifetimeRequestChannel channel = new();
        RecordingHandoff first = new("trigger-exit:1", []);
        RecordingHandoff duplicate = new("trigger-exit:1", []);

        Assert.True(channel.TryRequest(ApplicationLifetimeRequest.Exit("trigger", first)));
        Assert.True(channel.TryRequest(ApplicationLifetimeRequest.Exit("trigger", duplicate)));
        Assert.False(channel.TryRequest(ApplicationLifetimeRequest.Restart("settings")));

        ApplicationLifetimeRequest accepted = await channel.ReadAsync(CancellationToken.None);
        Assert.Same(first, accepted.Handoff);
    }

    [Fact]
    public async Task BusyOrdinaryRequest_AcceptsOnePendingDurableIdentityWithoutReplacement()
    {
        ApplicationLifetimeRequestChannel channel = new();
        ApplicationLifetimeRequest ordinary = ApplicationLifetimeRequest.Exit("main-window");
        ApplicationLifetimeRequest firstDurable = ApplicationLifetimeRequest.Exit(
            "trigger",
            new RecordingHandoff("trigger-exit:pending-first", []));
        ApplicationLifetimeRequest duplicateDurable = ApplicationLifetimeRequest.Exit(
            "trigger-duplicate",
            new RecordingHandoff("trigger-exit:pending-first", []));
        ApplicationLifetimeRequest secondDurable = ApplicationLifetimeRequest.Exit(
            "trigger-second",
            new RecordingHandoff("trigger-exit:pending-second", []));
        Assert.True(channel.TryRequest(ordinary));

        Assert.True(channel.TryRequest(firstDurable));
        Assert.True(channel.TryRequest(duplicateDurable));
        Assert.False(channel.TryRequest(secondDurable));
        Assert.Same(ordinary, await channel.ReadAsync(CancellationToken.None));

        Assert.False(await channel.RetryFailedRequestAsync(ordinary, CancellationToken.None));
        Assert.Same(firstDurable, await channel.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedRequest_ReleaseAllowsAReplacementRequest()
    {
        ApplicationLifetimeRequestChannel channel = new();
        ApplicationLifetimeRequest first = ApplicationLifetimeRequest.Exit("main-window");
        ApplicationLifetimeRequest replacement = ApplicationLifetimeRequest.Restart("settings");

        Assert.True(channel.TryRequest(first));
        Assert.Same(first, await channel.ReadAsync(CancellationToken.None));

        Assert.True(channel.TryReleaseFailedRequest(first));
        Assert.False(channel.HasAcceptedRequest);
        Assert.True(channel.TryRequest(replacement));
        Assert.Same(replacement, await channel.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmedFailedDurableRequest_DuplicateRemainsIdempotentWithoutBlockingUserRetry()
    {
        ApplicationLifetimeRequestChannel channel = new();
        List<string> trace = [];
        RecordingHandoff firstHandoff = new("trigger-exit:failed", trace);
        ApplicationLifetimeRequest first =
            ApplicationLifetimeRequest.Exit("trigger", firstHandoff);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(new FailOnceHost(trace));

        Assert.True(channel.TryRequest(first));
        Assert.Same(first, await channel.ReadAsync(CancellationToken.None));
        firstHandoff.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.ProcessAsync(first, CancellationToken.None));
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Confirmed,
            first.TerminalStatePersistence);
        Assert.True(channel.TryReleaseFailedRequest(first));

        Assert.True(channel.TryRequest(ApplicationLifetimeRequest.Exit(
            "trigger-duplicate",
            new RecordingHandoff("trigger-exit:failed", []))));
        Assert.False(channel.HasAcceptedRequest);

        ApplicationLifetimeRequest userRetry = ApplicationLifetimeRequest.Exit("system-tray");
        Assert.True(channel.TryRequest(userRetry));
        Assert.Same(userRetry, await channel.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HandoffFaultPoint.WaitForRelease)]
    [InlineData(HandoffFaultPoint.MarkShutdownStarted)]
    [InlineData(HandoffFaultPoint.MarkShutdownSucceeded)]
    [InlineData(HandoffFaultPoint.MarkShutdownFailed)]
    public async Task UnconfirmedHandoffCallbackFailure_AutomaticallyRequeuesExactRequestWithoutRepeatingHostStop(
        HandoffFaultPoint faultPoint)
    {
        ApplicationLifetimeRequestChannel channel = new();
        List<string> trace = [];
        FailOnceHandoff handoff = new("trigger-exit:callback-retry", faultPoint, trace);
        ApplicationLifetimeRequest first = ApplicationLifetimeRequest.Exit("trigger", handoff);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(faultPoint == HandoffFaultPoint.MarkShutdownFailed
            ? new FailOnceHost(trace)
            : new FakeHost(trace));

        Assert.True(channel.TryRequest(first));
        Assert.Same(first, await channel.ReadAsync(CancellationToken.None));
        handoff.Release();
        await Assert.ThrowsAnyAsync<Exception>(
            () => lifetime.ProcessAsync(first, CancellationToken.None));
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Unconfirmed,
            first.TerminalStatePersistence);
        Assert.True(await channel.RetryFailedRequestAsync(first, CancellationToken.None));
        Assert.True(channel.HasAcceptedRequest);
        ApplicationLifetimeRequest retry = await channel.ReadAsync(CancellationToken.None);
        Assert.Same(first, retry);

        if (faultPoint == HandoffFaultPoint.MarkShutdownFailed)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => lifetime.ProcessAsync(retry, CancellationToken.None));
            Assert.False(await channel.RetryFailedRequestAsync(retry, CancellationToken.None));
            Assert.True(lifetime.HasAttachedHost);
            Assert.False(channel.HasAcceptedRequest);
        }
        else
        {
            await lifetime.ProcessAsync(retry, CancellationToken.None);
            Assert.False(lifetime.HasAttachedHost);
        }

        Assert.Equal(1, trace.Count(entry => entry is "host-stop" or "host-stop-enter"));
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Confirmed,
            retry.TerminalStatePersistence);
    }

    [Fact]
    public async Task PersistentlyUnconfirmedDurableFailure_UsesDelayedBoundedRetriesThenReleasesActiveSlot()
    {
        ApplicationLifetimeRequestChannel channel = new();
        AlwaysFailingWaitHandoff handoff = new();
        ApplicationLifetimeRequest request = ApplicationLifetimeRequest.Exit("trigger", handoff);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(new FakeHost([]));
        Assert.True(channel.TryRequest(request));

        int processAttempts = 0;
        while (true)
        {
            Assert.Same(request, await channel.ReadAsync(CancellationToken.None));
            processAttempts++;
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => lifetime.ProcessAsync(request, CancellationToken.None));
            Task<bool> retry = channel.RetryFailedRequestAsync(
                request,
                CancellationToken.None).AsTask();
            if (processAttempts == 1)
            {
                Assert.False(retry.IsCompleted);
            }

            if (!await retry)
            {
                break;
            }
        }

        Assert.Equal(4, processAttempts);
        Assert.False(channel.HasAcceptedRequest);
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Unconfirmed,
            request.TerminalStatePersistence);
    }

    [Theory]
    [InlineData(HandoffFaultPoint.WaitForRelease)]
    [InlineData(HandoffFaultPoint.MarkShutdownStarted)]
    [InlineData(HandoffFaultPoint.MarkShutdownSucceeded)]
    [InlineData(HandoffFaultPoint.MarkShutdownFailed)]
    public async Task PersistentlyUnconfirmedDurableOwner_ReservesIdentityWithoutBlockingOrdinaryExit(
        HandoffFaultPoint faultPoint)
    {
        ApplicationLifetimeRequestChannel channel = new();
        PersistentlyFailingHandoff handoff = new(
            "trigger-exit:reserved-owner",
            faultPoint);
        ApplicationLifetimeRequest owner = ApplicationLifetimeRequest.Exit("trigger-owner", handoff);
        ApplicationLifetimeRequest competing = ApplicationLifetimeRequest.Exit(
            "trigger-competing",
            new RecordingHandoff("trigger-exit:competing", []));
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(faultPoint == HandoffFaultPoint.MarkShutdownFailed
            ? new FailOnceHost([])
            : new FakeHost([]));
        handoff.Release();

        Assert.True(channel.TryRequest(owner));
        Assert.False(channel.TryRequest(competing));
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            Assert.Same(owner, await channel.ReadAsync(CancellationToken.None));
            await Assert.ThrowsAnyAsync<Exception>(
                () => lifetime.ProcessAsync(owner, CancellationToken.None));
            bool retryScheduled = await channel.RetryFailedRequestAsync(
                owner,
                CancellationToken.None);
            Assert.Equal(attempt < 3, retryScheduled);
        }

        Assert.False(channel.HasAcceptedRequest);
        Assert.False(channel.TryRequest(competing));
        Assert.Equal(
            faultPoint != HandoffFaultPoint.MarkShutdownSucceeded,
            lifetime.CanResumeAttachedHost);

        ApplicationLifetimeRequest ordinaryExit = ApplicationLifetimeRequest.Exit("main-window");
        Assert.True(channel.TryRequest(ordinaryExit));
        Assert.Same(ordinaryExit, await channel.ReadAsync(CancellationToken.None));
        Assert.False(await channel.RetryFailedRequestAsync(
            ordinaryExit,
            CancellationToken.None));

        ApplicationLifetimeRequest sameIdentity = ApplicationLifetimeRequest.Exit(
            "trigger-reconciled",
            new RecordingHandoff("trigger-exit:reserved-owner", []));
        Assert.True(channel.TryRequest(sameIdentity));
        Assert.Same(sameIdentity, await channel.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreStopHandoffFailure_AttachedHostRemainsEligibleToResumeApplicationWork()
    {
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(new FakeHost([]));
        AlwaysFailingWaitHandoff handoff = new();
        ApplicationLifetimeRequest request = ApplicationLifetimeRequest.Exit("trigger", handoff);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.ProcessAsync(request, CancellationToken.None));

        Assert.True(lifetime.HasAttachedHost);
        Assert.True(lifetime.CanResumeAttachedHost);
    }

    [Fact]
    public async Task PersistentlyUnconfirmedShutdownSuccess_HostRemainsOwnedButCannotResumeApplicationWork()
    {
        ApplicationLifetimeRequestChannel channel = new();
        AlwaysFailingShutdownSucceededHandoff handoff = new();
        ApplicationLifetimeRequest request = ApplicationLifetimeRequest.Exit("trigger", handoff);
        List<string> trace = [];
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(new FakeHost(trace));
        Assert.True(channel.TryRequest(request));

        while (true)
        {
            Assert.Same(request, await channel.ReadAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => lifetime.ProcessAsync(request, CancellationToken.None));
            if (!await channel.RetryFailedRequestAsync(request, CancellationToken.None))
            {
                break;
            }
        }

        Assert.True(lifetime.HasAttachedHost);
        Assert.False(lifetime.CanResumeAttachedHost);
        Assert.False(channel.HasAcceptedRequest);
        Assert.Equal(1, trace.Count(entry => entry == "host-stop-enter"));
        Assert.DoesNotContain("host-dispose", trace);
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Unconfirmed,
            request.TerminalStatePersistence);
    }

    [Fact]
    public async Task ProcessAsync_DurableHandoff_RecordsShutdownAfterReleaseAndBeforeDisposal()
    {
        List<string> trace = [];
        RecordingHandoff handoff = new("trigger-exit:2", trace);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(new FakeHost(trace));
        ApplicationLifetimeRequest request =
            ApplicationLifetimeRequest.Exit("trigger", handoff);

        Task processing = lifetime.ProcessAsync(
            request,
            CancellationToken.None);
        await Task.Yield();
        Assert.Empty(trace);
        handoff.Release();
        await processing;

        Assert.Equal(
            [
                "handoff-released",
                "handoff-shutdown-started",
                "host-stop-enter",
                "host-stop-unwound",
                "handoff-shutdown-succeeded",
                "host-dispose",
            ],
            trace);
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Confirmed,
            request.TerminalStatePersistence);
    }

    [Fact]
    public async Task ProcessAsync_DurableHandoffWithoutAttachedHost_FailsExplicitly()
    {
        ProcessLifetimeRunner lifetime = new();
        RecordingHandoff handoff = new("trigger-exit:missing-host", []);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifetime.ProcessAsync(
                ApplicationLifetimeRequest.Exit("trigger", handoff),
                CancellationToken.None));

        Assert.Contains("without an attached host", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_FirstHostStopFails_AllowsLaterRetryWithoutLosingOwnership()
    {
        List<string> trace = [];
        FailOnceHost host = new(trace);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.StopAsync(CancellationToken.None));

        Assert.True(lifetime.HasAttachedHost);
        Assert.True(lifetime.CanResumeAttachedHost);
        await lifetime.StopAsync(CancellationToken.None);

        Assert.Equal(["host-stop", "host-stop", "host-dispose"], trace);
        Assert.False(lifetime.HasAttachedHost);
    }

    [Fact]
    public async Task ProcessAsync_DurableShutdownFails_LaterUserStopRetriesWithoutRewritingHandoff()
    {
        List<string> trace = [];
        RecordingHandoff handoff = new("trigger-exit:retry-owner", trace);
        FailOnceHost host = new(trace);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);
        handoff.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifetime.ProcessAsync(
                ApplicationLifetimeRequest.Exit("trigger", handoff),
                CancellationToken.None));
        await lifetime.StopAsync(CancellationToken.None);

        Assert.Equal(
            [
                "handoff-released",
                "handoff-shutdown-started",
                "host-stop",
                "handoff-shutdown-failed:Uncertain:trigger.handoff.shutdown_unexpected",
                "host-stop",
                "host-dispose",
            ],
            trace);
        Assert.DoesNotContain("handoff-shutdown-succeeded", trace);
    }

    [Fact]
    public async Task ProcessAsync_ConfirmedFailedRequestCannotRewriteTerminalHandoff()
    {
        List<string> trace = [];
        RecordingHandoff handoff = new("trigger-exit:confirmed-terminal", trace);
        ApplicationLifetimeRequest request = ApplicationLifetimeRequest.Exit("trigger", handoff);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(new FailOnceHost(trace));
        handoff.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.ProcessAsync(request, CancellationToken.None));
        string[] terminalTrace = [.. trace];

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.ProcessAsync(request, CancellationToken.None));

        Assert.Contains("terminal state", exception.Message, StringComparison.Ordinal);
        Assert.Equal(terminalTrace, trace);
        Assert.True(lifetime.HasAttachedHost);
        await lifetime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProcessAsync_ConfirmedTerminalFailure_AllowsDifferentDurableIdentityToOwnLaterRetry()
    {
        List<string> trace = [];
        FailOnceHost host = new(trace);
        RecordingHandoff firstHandoff = new("trigger-exit:first-terminal", trace);
        ApplicationLifetimeRequest first = ApplicationLifetimeRequest.Exit("trigger-first", firstHandoff);
        RecordingHandoff secondHandoff = new("trigger-exit:second-retry", trace);
        ApplicationLifetimeRequest second = ApplicationLifetimeRequest.Exit("trigger-second", secondHandoff);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);
        firstHandoff.Release();
        secondHandoff.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.ProcessAsync(first, CancellationToken.None));
        await lifetime.ProcessAsync(second, CancellationToken.None);

        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Confirmed,
            first.TerminalStatePersistence);
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Confirmed,
            second.TerminalStatePersistence);
        Assert.Equal(2, trace.Count(entry => entry == "host-stop"));
        Assert.False(lifetime.HasAttachedHost);
    }

    [Fact]
    public async Task ProcessAsync_UnconfirmedTerminalCallbackFailure_KeepsDifferentDurableIdentityOut()
    {
        List<string> trace = [];
        FailOnceHandoff firstHandoff = new(
            "trigger-exit:unconfirmed-owner",
            HandoffFaultPoint.MarkShutdownFailed,
            trace);
        ApplicationLifetimeRequest first = ApplicationLifetimeRequest.Exit("trigger-first", firstHandoff);
        RecordingHandoff competingHandoff = new("trigger-exit:competing", trace);
        ApplicationLifetimeRequest competing =
            ApplicationLifetimeRequest.Exit("trigger-competing", competingHandoff);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(new FailOnceHost(trace));
        firstHandoff.Release();
        competingHandoff.Release();
        await Assert.ThrowsAnyAsync<Exception>(
            () => lifetime.ProcessAsync(first, CancellationToken.None));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.ProcessAsync(competing, CancellationToken.None));

        Assert.Contains("different durable lifetime request", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Unconfirmed,
            first.TerminalStatePersistence);
        Assert.Equal(1, trace.Count(entry => entry == "host-stop"));
        Assert.True(lifetime.HasAttachedHost);
    }

    [Fact]
    public async Task ProcessAsync_RealAppHostDisposeFailsAfterDurableSuccess_PreservesTerminalOutcome()
    {
        ApplicationLifetimeRequestChannel channel = new();
        List<string> trace = [];
        RecordingHandoff handoff = new("trigger-exit:dispose-terminal", trace);
        ApplicationLifetimeRequest request = ApplicationLifetimeRequest.Exit("trigger", handoff);
        DisposeFailingShutdownCoordinator shutdown = new();
        AppHost host = AppHost.Build(services =>
            services.AddSingleton<IApplicationShutdownCoordinator>(_ => shutdown));
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);

        Assert.True(channel.TryRequest(request));
        Assert.Same(request, await channel.ReadAsync(CancellationToken.None));
        handoff.Release();
        Task processing = lifetime.ProcessAsync(request, CancellationToken.None);
        ApplicationHostDisposalException exception =
            await Assert.ThrowsAsync<ApplicationHostDisposalException>(() => processing);

        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Confirmed,
            request.TerminalStatePersistence);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.False(lifetime.HasAttachedHost);
        Assert.True(channel.TryReleaseFailedRequest(request));
        Assert.True(channel.TryRequest(ApplicationLifetimeRequest.Exit(
            "trigger-duplicate",
            new RecordingHandoff("trigger-exit:dispose-terminal", []))));
        Assert.False(channel.HasAcceptedRequest);
        Assert.Same(processing, lifetime.StopAsync(CancellationToken.None));
        Assert.Equal(1, shutdown.StopCallCount);
        Assert.Equal(1, shutdown.DisposeCallCount);
    }

    [Fact]
    public void DurableHandoff_RequiresNonemptyIdempotencyKey()
    {
        RecordingHandoff handoff = new(" ", []);

        Assert.Throws<ArgumentException>(() => ApplicationLifetimeRequest.Exit("trigger", handoff));
    }

    private sealed class FakeHost(List<string> trace) : IApplicationHost
    {
        public Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(StartupStepResult.Succeeded());
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            trace.Add("host-stop-enter");
            await Task.Yield();
            trace.Add("host-stop-unwound");
        }

        public ValueTask DisposeAsync()
        {
            trace.Add("host-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceHost(List<string> trace) : IApplicationHost
    {
        private int _remainingFailures = 1;

        public Task<StartupStepResult> StartAsync(
            AppLaunchRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(StartupStepResult.Succeeded());
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("host-stop");
            if (Interlocked.Exchange(ref _remainingFailures, 0) != 0)
            {
                throw new InvalidOperationException("not prepared");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            trace.Add("host-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposeFailingShutdownCoordinator :
        IApplicationShutdownCoordinator,
        IAsyncDisposable
    {
        private int _disposeCallCount;
        private int _stopCallCount;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public int StopCallCount => Volatile.Read(ref _stopCallCount);

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _stopCallCount);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            return ValueTask.FromException(
                new InvalidOperationException("Injected service disposal failure."));
        }
    }

    private sealed class RecordingHandoff(
        string idempotencyKey,
        List<string> trace) : IApplicationLifetimeHandoff
    {
        private readonly TaskCompletionSource<object?> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string IdempotencyKey { get; } = idempotencyKey;

        public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            await _released.Task.WaitAsync(cancellationToken);
            trace.Add("handoff-released");
        }

        public Task MarkShutdownStartedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("handoff-shutdown-started");
            return Task.CompletedTask;
        }

        public Task MarkShutdownSucceededAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("handoff-shutdown-succeeded");
            return Task.CompletedTask;
        }

        public Task MarkShutdownFailedAsync(
            ApplicationLifetimeShutdownFailureKind failureKind,
            string diagnosticCode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"handoff-shutdown-failed:{failureKind}:{diagnosticCode}");
            return Task.CompletedTask;
        }

        public void Release() => _released.TrySetResult(null);
    }

    public enum HandoffFaultPoint
    {
        WaitForRelease,
        MarkShutdownStarted,
        MarkShutdownSucceeded,
        MarkShutdownFailed,
    }

    private sealed class FailOnceHandoff(
        string idempotencyKey,
        HandoffFaultPoint faultPoint,
        List<string> trace) : IApplicationLifetimeHandoff
    {
        private readonly TaskCompletionSource<object?> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remainingFailures = 1;

        public string IdempotencyKey { get; } = idempotencyKey;

        public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            await _released.Task.WaitAsync(cancellationToken);
            ThrowOnce(HandoffFaultPoint.WaitForRelease);
            trace.Add("handoff-released");
        }

        public Task MarkShutdownStartedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowOnce(HandoffFaultPoint.MarkShutdownStarted);
            trace.Add("handoff-shutdown-started");
            return Task.CompletedTask;
        }

        public Task MarkShutdownSucceededAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowOnce(HandoffFaultPoint.MarkShutdownSucceeded);
            trace.Add("handoff-shutdown-succeeded");
            return Task.CompletedTask;
        }

        public Task MarkShutdownFailedAsync(
            ApplicationLifetimeShutdownFailureKind failureKind,
            string diagnosticCode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowOnce(HandoffFaultPoint.MarkShutdownFailed);
            trace.Add($"handoff-shutdown-failed:{failureKind}:{diagnosticCode}");
            return Task.CompletedTask;
        }

        public void Release() => _released.TrySetResult(null);

        private void ThrowOnce(HandoffFaultPoint currentFaultPoint)
        {
            if (faultPoint == currentFaultPoint
                && Interlocked.Exchange(ref _remainingFailures, 0) != 0)
            {
                throw new InvalidOperationException($"Injected {currentFaultPoint} failure.");
            }
        }
    }

    private sealed class AlwaysFailingWaitHandoff : IApplicationLifetimeHandoff
    {
        public string IdempotencyKey => "trigger-exit:persistent-callback-failure";

        public Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException(new InvalidOperationException("Injected persistent wait failure."));
        }

        public Task MarkShutdownStartedAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Shutdown must not start when release observation fails.");

        public Task MarkShutdownSucceededAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Shutdown must not succeed when release observation fails.");

        public Task MarkShutdownFailedAsync(
            ApplicationLifetimeShutdownFailureKind failureKind,
            string diagnosticCode,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Shutdown must not fail when release observation fails.");
    }

    private sealed class PersistentlyFailingHandoff(
        string idempotencyKey,
        HandoffFaultPoint faultPoint) : IApplicationLifetimeHandoff
    {
        private readonly TaskCompletionSource<object?> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string IdempotencyKey { get; } = idempotencyKey;

        public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            await _released.Task.WaitAsync(cancellationToken);
            ThrowAt(HandoffFaultPoint.WaitForRelease);
        }

        public Task MarkShutdownStartedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowAt(HandoffFaultPoint.MarkShutdownStarted);
            return Task.CompletedTask;
        }

        public Task MarkShutdownSucceededAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowAt(HandoffFaultPoint.MarkShutdownSucceeded);
            return Task.CompletedTask;
        }

        public Task MarkShutdownFailedAsync(
            ApplicationLifetimeShutdownFailureKind failureKind,
            string diagnosticCode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowAt(HandoffFaultPoint.MarkShutdownFailed);
            return Task.CompletedTask;
        }

        public void Release() => _released.TrySetResult(null);

        private void ThrowAt(HandoffFaultPoint currentFaultPoint)
        {
            if (faultPoint == currentFaultPoint)
            {
                throw new InvalidOperationException(
                    $"Injected persistent {currentFaultPoint} failure.");
            }
        }
    }

    private sealed class AlwaysFailingShutdownSucceededHandoff : IApplicationLifetimeHandoff
    {
        public string IdempotencyKey => "trigger-exit:persistent-success-callback-failure";

        public Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task MarkShutdownStartedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task MarkShutdownSucceededAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException(
                new InvalidOperationException("Injected persistent success callback failure."));
        }

        public Task MarkShutdownFailedAsync(
            ApplicationLifetimeShutdownFailureKind failureKind,
            string diagnosticCode,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A successful host stop must not be recorded as failed.");
    }

}

using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Tests.Architecture;

/// <summary>Verifies worker-originated lifetime requests hand off before host shutdown begins.</summary>
public sealed class ApplicationLifetimeRequestTests
{
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
    public async Task ProcessAsync_DurableHandoff_RecordsShutdownAfterReleaseAndBeforeDisposal()
    {
        List<string> trace = [];
        RecordingHandoff handoff = new("trigger-exit:2", trace);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(new FakeHost(trace));

        Task processing = lifetime.ProcessAsync(
            ApplicationLifetimeRequest.Exit("trigger", handoff),
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
}

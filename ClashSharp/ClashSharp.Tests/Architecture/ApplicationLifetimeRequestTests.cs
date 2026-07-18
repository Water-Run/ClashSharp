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
}

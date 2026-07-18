using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace ClashSharp.Tests.Architecture;

/// <summary>Specifies primary-instance, host-construction, startup-order, and outer-lifetime contracts.</summary>
public sealed class ApplicationStartupContractTests
{
    /// <summary>Verifies a redirected secondary launch cannot construct or start the application host.</summary>
    [Fact]
    public async Task LaunchAsync_SecondaryInstance_DoesNotConstructHost()
    {
        List<string> trace = [];
        FakePrimaryInstanceBootstrap primaryInstance = new(PrimaryInstanceOwnership.Redirected, trace);
        ProcessLifetimeRunner lifetime = new();
        int hostFactoryCalls = 0;
        ApplicationBootstrapper bootstrapper = new(
            primaryInstance,
            () =>
            {
                hostFactoryCalls++;
                trace.Add("host-build");
                return new FakeApplicationHost(trace);
            },
            lifetime);

        ApplicationLaunchResult result = await bootstrapper.LaunchAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(ApplicationLaunchDisposition.Redirected, result.Disposition);
        Assert.Equal(0, hostFactoryCalls);
        Assert.Equal(["arbitrate"], trace);
        Assert.False(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies primary arbitration completes before host construction and startup.</summary>
    [Fact]
    public async Task LaunchAsync_PrimaryInstance_BuildsAndStartsHostAfterArbitration()
    {
        List<string> trace = [];
        FakeApplicationHost host = new(trace);
        ProcessLifetimeRunner lifetime = new();
        ApplicationBootstrapper bootstrapper = new(
            new FakePrimaryInstanceBootstrap(PrimaryInstanceOwnership.Primary, trace),
            () =>
            {
                trace.Add("host-build");
                return host;
            },
            lifetime);

        ApplicationLaunchResult result = await bootstrapper.LaunchAsync(new AppLaunchRequest("--test"), CancellationToken.None);

        Assert.Equal(ApplicationLaunchDisposition.Running, result.Disposition);
        Assert.Equal(["arbitrate", "host-build", "host-start"], trace);
        Assert.True(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies a host that throws during startup is stopped and disposed without attachment.</summary>
    [Fact]
    public async Task LaunchAsync_StartupFails_StopsAndDisposesUnattachedHost()
    {
        List<string> trace = [];
        FakeApplicationHost host = new(trace) { StartException = new InvalidOperationException("startup failed") };
        ProcessLifetimeRunner lifetime = new();
        ApplicationBootstrapper bootstrapper = new(
            new FakePrimaryInstanceBootstrap(PrimaryInstanceOwnership.Primary, trace),
            () =>
            {
                trace.Add("host-build");
                return host;
            },
            lifetime);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bootstrapper.LaunchAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Equal(["arbitrate", "host-build", "host-start", "host-stop", "host-dispose"], trace);
        Assert.False(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies building AppHost does not instantiate the registered startup coordinator.</summary>
    [Fact]
    public async Task Build_DoesNotResolveStartupCoordinatorUntilStart()
    {
        int constructorCalls = 0;
        await using AppHost host = AppHost.Build(services =>
            services.AddSingleton<IApplicationStartupCoordinator>(_ => new FakeStartupCoordinator(() => constructorCalls++)));

        Assert.Equal(0, constructorCalls);

        StartupStepResult result = await host.StartAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, constructorCalls);
    }

    /// <summary>Verifies startup steps execute deterministically and stop after an exit request.</summary>
    [Fact]
    public async Task StartAsync_OrdersStepsAndStopsAfterExitRequest()
    {
        List<string> trace = [];
        StartupCoordinator coordinator = new(
        [
            new FakeStartupStep("window", 30, StartupStepResult.Succeeded(), trace),
            new FakeStartupStep("helper", 20, StartupStepResult.ExitRequested(), trace),
            new FakeStartupStep("configure", 10, StartupStepResult.Succeeded(), trace),
            new FakeStartupStep("audit", 20, StartupStepResult.Succeeded(), trace),
        ]);

        StartupStepResult result = await coordinator.StartAsync(new AppLaunchRequest("--helper"), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.ExitRequested, result.Outcome);
        Assert.Equal(["configure", "audit", "helper"], trace);
    }

    /// <summary>Verifies asynchronous startup work resumes through the caller's UI synchronization context.</summary>
    [Fact]
    public async Task StartAsync_AsynchronousStep_PreservesCallerSynchronizationContext()
    {
        RecordingSynchronizationContext context = new();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task<StartupStepResult> startupTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            StartupCoordinator coordinator = new(
            [
                new AsynchronousStartupStep(),
                new FakeStartupStep("window", 20, StartupStepResult.Succeeded(), []),
            ]);
            startupTask = coordinator.StartAsync(new AppLaunchRequest(""), CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        StartupStepResult result = await startupTask;

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        Assert.True(context.PostCount > 0);
    }

    /// <summary>Verifies concurrent lifetime stop requests stop and dispose one attached host once.</summary>
    [Fact]
    public async Task StopAsync_ConcurrentCallers_StopThenDisposeHostOnce()
    {
        List<string> trace = [];
        FakeApplicationHost host = new(trace);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => lifetime.StopAsync(CancellationToken.None)));

        Assert.Equal(["host-stop", "host-dispose"], trace);
        Assert.False(lifetime.HasAttachedHost);
    }

    private sealed class FakePrimaryInstanceBootstrap(
        PrimaryInstanceOwnership ownership,
        ICollection<string> trace) : IPrimaryInstanceBootstrap
    {
        public Task<PrimaryInstanceOwnership> AcquireAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("arbitrate");
            return Task.FromResult(ownership);
        }
    }

    private sealed class FakeApplicationHost(ICollection<string> trace) : IApplicationHost
    {
        public Exception? StartException { get; init; }

        public Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            trace.Add("host-start");
            return StartException is null
                ? Task.FromResult(StartupStepResult.Succeeded())
                : Task.FromException<StartupStepResult>(StartException);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            trace.Add("host-stop");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            trace.Add("host-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStartupCoordinator(Action onConstructed) : IApplicationStartupCoordinator
    {
        private readonly object _constructionProbe = Invoke(onConstructed);

        public Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            GC.KeepAlive(_constructionProbe);
            return Task.FromResult(StartupStepResult.Succeeded());
        }

        private static object Invoke(Action action)
        {
            action();
            return new object();
        }
    }

    private sealed class FakeStartupStep(
        string name,
        int order,
        StartupStepResult result,
        ICollection<string> trace) : IStartupStep
    {
        public string Name => name;

        public int Order => order;

        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            trace.Add(Name);
            return Task.FromResult(result);
        }
    }

    private sealed class AsynchronousStartupStep : IStartupStep
    {
        public string Name => "asynchronous";

        public int Order => 10;

        public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            return StartupStepResult.Succeeded();
        }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}

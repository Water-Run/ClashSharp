extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Settings;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task ReplacementRollback_KeepsOrdinaryMutationsClosedUntilEveryProducerHasResumed()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        var compose = fixture.ComposeRuntime!;
        ResumeProbe baselineProducer = new();
        fixture.ComposeRuntime = async (repositories, token) =>
        {
            await compose(repositories, token);
            if (repositories.Generation.GenerationNumber == 1) { repositories.OwnProducer(baselineProducer); }
        };
        var settings = new UiService.AppSettingsService(new Dictionary<string, object>());
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(fixture.Manager), settings).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        await baselineProducer.StartAsync(CancellationToken.None);
        network.BeforeApply = (target, _) => { if (target.MixedPort == 10000) { throw new IOException("candidate rejected"); } };

        Task replacement = fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);
        try
        {
            await baselineProducer.ResumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, baselineProducer.QuiesceCount);
            Assert.False(replacement.IsCompleted);
            Assert.Equal(MutationAdmissionState.Closing, fixture.Admission.State);
            Assert.Throws<MutationAdmissionRejectedException>(() => fixture.Admission.AcquireOrdinary());
        }
        finally
        {
            baselineProducer.ReleaseResume.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => replacement);
        }

        Assert.True(baselineProducer.Running);
        Assert.True(RuntimeOf(fixture.Containers[0]).IsExecutionPublished);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.Equal(23456, settings.MixedPort);
    }

    [Fact]
    public async Task ReplacementRollback_CancellationDuringQuiescenceWaitsThenRestoresTheOriginalProducer()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ResumeProbe producer = new() { HoldQuiescence = true };
        producer.ReleaseResume.SetResult();
        await StartResumeFixtureAsync(fixture, producer);
        string baseline = fixture.Manager.CurrentManifest.ContentHash;
        using CancellationTokenSource cancellation = new();
        Task replacement = fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(cancellation.Token);
        try
        {
            await producer.QuiesceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(producer.QuiesceToken.CanBeCanceled);
            Assert.False(replacement.IsCompleted);
            Assert.Equal(MutationAdmissionState.Closing, fixture.Admission.State);
        }
        finally { producer.ReleaseQuiescence.TrySetResult(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement);
        Assert.True(producer.Running);
        Assert.Equal(baseline, fixture.Manager.CurrentManifest.ContentHash);
        Assert.True(RuntimeOf(fixture.Containers[0]).IsExecutionPublished);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task ReplacementRollback_UnknownQuiescenceFailureRetainsTheCheckpointAndClosesMutations()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ResumeProbe producer = new() { FailQuiescence = true };
        await StartResumeFixtureAsync(fixture, producer);
        string baseline = fixture.Manager.CurrentManifest.ContentHash;

        var failure = await Assert.ThrowsAsync<UiData.GenerationReplacementRecoveryException>(() =>
            fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None));

        Assert.False(failure.IsCommitted);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Admission.State);
        Assert.Throws<MutationAdmissionRejectedException>(() => fixture.Admission.AcquireOrdinary());
        Assert.Equal(baseline, fixture.Manager.CurrentManifest.ContentHash);
        Assert.False(producer.Running);
        Assert.False(producer.ResumeEntered.Task.IsCompleted);
        var checkpoint = await new UiData.FileGenerationReplacementJournal(directory.RootPath).ReadPendingAsync(CancellationToken.None);
        Assert.NotNull(checkpoint);
    }

    private static async Task StartResumeFixtureAsync(Fixture fixture, ResumeProbe producer)
    {
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        var compose = fixture.ComposeRuntime!;
        fixture.ComposeRuntime = async (repositories, token) =>
        {
            await compose(repositories, token);
            if (repositories.Generation.GenerationNumber == 1) { repositories.OwnProducer(producer); }
        };
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(fixture.Manager), new UiService.AppSettingsService(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        await producer.StartAsync(CancellationToken.None);
    }

    private sealed class ResumeProbe : IRuntimeParticipant
    {
        public string Name => "additional-owned-producer";
        public bool Running { get; private set; }
        public int QuiesceCount { get; private set; }
        public bool HoldQuiescence { get; init; }
        public bool FailQuiescence { get; init; }
        public CancellationToken QuiesceToken { get; private set; }
        public TaskCompletionSource QuiesceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseQuiescence { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseResume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken) { Running = true; return Task.CompletedTask; }
        public async Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
        {
            ++QuiesceCount;
            bool wasRunning = Running;
            Running = false;
            QuiesceToken = cancellationToken;
            QuiesceEntered.TrySetResult();
            if (HoldQuiescence) { await ReleaseQuiescence.Task.WaitAsync(cancellationToken); }
            if (FailQuiescence) { throw new IOException("producer state cannot be verified"); }
            return new QuiescedState(wasRunning);
        }
        public async Task ResumeAsync(QuiescedState state, CancellationToken cancellationToken)
        {
            ResumeEntered.TrySetResult();
            await ReleaseResume.Task.WaitAsync(cancellationToken);
            Running = state.WasRunning;
        }
        public Task StopAsync(CancellationToken cancellationToken) { Running = false; return Task.CompletedTask; }
    }
}

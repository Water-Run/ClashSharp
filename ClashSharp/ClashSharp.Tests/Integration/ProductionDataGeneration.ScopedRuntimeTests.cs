extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiModel = ClashSharpUi::ClashSharp.Model;
using UiService = ClashSharpUi::ClashSharp.Service;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task PreparedCandidate_IsClaimedBeforeShutdownCanRetireItsRepositories()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var baseline = await fixture.StartAsync();
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DataGenerationDescriptor> preparing = transition.PrepareAndStageAsync(async _ =>
        {
            DataGenerationScope candidate = await fixture.CreateEmptyScopeAsync(admission);
            entered.TrySetResult();
            await release.Task;
            return candidate;
        }, CancellationToken.None);
        Task? closing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            closing = fixture.Manager.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.All(fixture.Participants, participant => Assert.Equal(0, participant.Disposals));
        }
        finally { release.TrySetResult(); }
        DataGenerationDescriptor prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(5));
        await closing!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(prepared.GenerationId, fixture.Containers[^1].Generation.GenerationId);
        Assert.All(fixture.Participants, participant => Assert.Equal(1, participant.Disposals));
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[^1].Logs.GetRecentLogs(5));
        Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    [Fact]
    public async Task CancelledPreparation_RetiresReturnedUnclaimedScopeAndAllowsAbort()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var baseline = await fixture.StartAsync();
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transition.PrepareAndStageAsync(async _ =>
        {
            DataGenerationScope candidate = await fixture.CreateEmptyScopeAsync(admission);
            cancellation.Cancel();
            return candidate;
        }, cancellation.Token));
        Assert.Null(transition.StagedDescriptor);
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[^1].Session.Snapshot);
        Assert.NotEmpty(fixture.Containers[0].Session.Snapshot.Desired);
        await transition.AbortAsync(directory.Store, CancellationToken.None);
        Assert.Equal(baseline.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
    }

    [Fact]
    public async Task StagedReset_UsesProductionPreparationAndRejectsAnotherFactoryBeforeItRuns()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var baseline = await fixture.StartAsync();
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);

        DataGenerationDescriptor prepared = await fixture.CreateCandidatePreparer().StageResetAdmittedAsync(transition, admission, CancellationToken.None);

        Assert.True(prepared.IsSameGeneration(transition.StagedDescriptor!));
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => transition.PrepareAndStageAsync(_ =>
        {
            ++calls;
            throw new InvalidOperationException("must not run");
        }, CancellationToken.None));
        Assert.Equal(0, calls);
        await transition.AbortAsync(directory.Store, CancellationToken.None);
        Assert.Equal(baseline.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
    }

    [Fact]
    public async Task PreparationFailure_ReleasesTheOperationSlotWithoutPublishing()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var baseline = await fixture.StartAsync();
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => transition.PrepareAndStageAsync(
            _ => Task.FromException<DataGenerationScope>(new IOException("preparation failed")), CancellationToken.None));
        Assert.Null(transition.StagedDescriptor);
        Assert.Single(fixture.Containers);
        await transition.AbortAsync(directory.Store, CancellationToken.None);
        Assert.Equal(baseline.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
    }

    [Fact]
    public async Task TransitionOperations_TargetOwnedRepositoriesWithoutPublishingOrOpeningReaders()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var baseline = await fixture.StartAsync();
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transition.ExecuteCandidateAsync<UiService.ILogStorage, int>((_, _, _) => Task.FromResult(0), CancellationToken.None));
        transition.Stage(await fixture.CreateEmptyScopeAsync(admission));

        Guid candidate = await transition.ExecuteCandidateAsync<UiService.ILogStorage, Guid>((logs, descriptor, _) =>
        {
            logs.AppendLog("Info", "Transition", "candidate", null);
            return Task.FromResult(descriptor.GenerationId);
        }, CancellationToken.None);
        Guid source = await transition.ExecuteBaselineAsync<UiService.ILogStorage, Guid>((logs, descriptor, _) =>
        {
            logs.AppendLog("Info", "Transition", "baseline", null);
            return Task.FromResult(descriptor.GenerationId);
        }, CancellationToken.None);

        Assert.Equal(baseline.Descriptor.GenerationId, source);
        Assert.NotEqual(source, candidate);
        Assert.Equal("candidate", Assert.Single(fixture.Containers[^1].Logs.GetRecentLogs(5)).Message);
        Assert.Throws<DataGenerationManagerException>(() => new UiData.GenerationLogStorage(fixture.Manager).GetRecentLogs(5));
        Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        await transition.AbortAsync(directory.Store, CancellationToken.None);
        Assert.Equal("baseline", Assert.Single(new UiData.GenerationLogStorage(fixture.Manager).GetRecentLogs(5)).Message);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transition.ExecuteCandidateAsync<UiService.ILogStorage, int>((_, _, _) => Task.FromResult(0), CancellationToken.None));
    }

    [Fact]
    public async Task TransitionOperation_BlocksCompetingResolutionAndShutdownUntilAcceptedWorkFinishes()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var baseline = await fixture.StartAsync();
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        transition.Stage(await fixture.CreateEmptyScopeAsync(admission));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        Task<int> accepted = transition.ExecuteCandidateAsync<UiService.ILogStorage, int>(async (logs, _, token) =>
        {
            entered.TrySetResult();
            await release.Task;
            Assert.True(token.IsCancellationRequested);
            logs.AppendLog("Info", "Transition", "accepted completion", null);
            return logs.GetRecentLogs(5).Count;
        }, cancellation.Token);
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<DataGenerationManagerException>(() => transition.PromoteManifestAsync(directory.Store, CancellationToken.None));
            await Assert.ThrowsAsync<DataGenerationManagerException>(() => transition.ExecuteBaselineAsync<UiService.ILogStorage, int>((_, _, _) => Task.FromResult(0), CancellationToken.None));
            cancellation.Cancel();
            shutdown = fixture.Manager.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.False(accepted.IsCompleted);
            Assert.All(fixture.Participants, participant => Assert.Equal(0, participant.Disposals));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await accepted.WaitAsync(TimeSpan.FromSeconds(5)));
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(fixture.Participants, participant => Assert.Equal(1, participant.Disposals));
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[^1].Logs.GetRecentLogs(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledTransitionOperation_ReleasesOwnershipForVerifiedAbort(bool cancelled)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var baseline = await fixture.StartAsync();
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        transition.Stage(await fixture.CreateEmptyScopeAsync(admission));
        using CancellationTokenSource cancellation = new();
        Task<int> failure = transition.ExecuteCandidateAsync<UiService.ILogStorage, int>((logs, _, token) =>
        {
            logs.AppendLog("Info", "Transition", "incomplete candidate", null);
            if (cancelled) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new IOException("owned effect failed");
        }, cancellation.Token);
        if (cancelled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failure); }
        else { await Assert.ThrowsAsync<IOException>(() => failure); }
        await transition.AbortAsync(directory.Store, CancellationToken.None);
        Assert.Equal(baseline.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
        Assert.Empty(new UiData.GenerationLogStorage(fixture.Manager).GetRecentLogs(5));
    }

    [Fact]
    public async Task RuntimeComposition_BindsDifferentNativeViewsToTheirOwnConfigurationIdentities()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(fixture.Manager), new UiService.AppSettingsService(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        var original = Assert.IsType<UiData.AppDataGenerationRuntime>(fixture.Containers[0].GetService(typeof(UiData.AppDataGenerationRuntime)));
        Assert.Equal(original.Repositories.Generation.GenerationId, original.Takeover.DataGenerationId);

        await fixture.ReplaceWithEmptyGenerationAsync();

        var replacement = Assert.IsType<UiData.AppDataGenerationRuntime>(fixture.Containers[^1].GetService(typeof(UiData.AppDataGenerationRuntime)));
        Assert.Equal(replacement.Repositories.Generation.GenerationId, replacement.Takeover.DataGenerationId);
        Assert.NotSame(original.Takeover, replacement.Takeover);
        Assert.NotEqual(original.Takeover.DataGenerationId, replacement.Takeover.DataGenerationId);
        UiData.GenerationProfileRuntime profile = new(replacement.Repositories.Session, replacement.Repositories.Configuration);
        Assert.Throws<InvalidOperationException>(() => profile.BindTakeover(original.Takeover));
        profile.BindTakeover(replacement.Takeover);
        Assert.Throws<InvalidOperationException>(() => profile.BindTakeover(replacement.Takeover));
    }

    private static UiService.NetworkTakeoverService CreateUnusedTakeover()
    {
        UnusedTakeoverPorts ports = new();
        return new(ports, ports, ports, ports, ports, ports, key => key);
    }

    /// <summary>Constructible native boundaries that fail if an isolated startup fixture accidentally invokes Windows effects.</summary>
    private sealed class UnusedTakeoverPorts : UiService.INetworkTakeoverCoreConfiguration, UiService.INetworkTakeoverCore,
        UiService.INetworkTakeoverWindowsProxy, UiService.INetworkTakeoverMihomoService,
        UiService.INetworkTakeoverProxyRecovery, UiService.INetworkTakeoverReadiness
    {
        public bool IsRunning => throw new NotSupportedException();
        public bool IsOwnershipKnown => throw new NotSupportedException();
        public void Restart(UiModel.CoreConfigurationState configuration) => throw new NotSupportedException();
        public void Stop() => throw new NotSupportedException();
        public void DisableProxy() => throw new NotSupportedException();
        public void EnableProxy(string server) => throw new NotSupportedException();
        public string BuildLoopbackProxyServer(int port) => throw new NotSupportedException();
        public Task<UiModel.MihomoServiceStatus> GetStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<UiModel.MihomoServiceStatus> RestartAsync(long generation, string hash, CancellationToken token) => throw new NotSupportedException();
        public Task<UiModel.MihomoServiceStatus> StopAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<bool> MatchesRuntimeConfigurationAsync(UiService.RuntimeConfigurationActivationPlan plan, long generation,
            string hash, UiModel.MihomoServiceStatus service, CancellationToken token) => throw new NotSupportedException();
        public Task<UiService.RuntimeConfigurationTransactionResult> ApplyConfigurationAsync(ClashSharpMode mode, bool tun,
            int port, UiService.ICoreConfigurationRuntime runtime, CancellationToken token) => throw new NotSupportedException();
    }
}

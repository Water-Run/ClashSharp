extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
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
    public async Task ReplacementRuntime_VerifiesLiveSettingsButCannotPublishUntilCommittedAndAdmissionReopens()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(fixture.Manager), new UiService.AppSettingsService(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        var original = Assert.IsType<UiData.AppDataGenerationRuntime>(fixture.Containers[0].GetService(typeof(UiData.AppDataGenerationRuntime)));
        Assert.True(original.IsExecutionPublished);
        var baseline = fixture.Manager.CurrentManifest;
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        _ = await original.TriggerSettings.Scheduler.QuiesceAsync(CancellationToken.None);
        _ = await original.Sampling.QuiesceAsync(CancellationToken.None);
        _ = await original.Subscriptions.QuiesceAsync(CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        try
        {
            _ = await fixture.CreateCandidatePreparer().StageResetAdmittedAsync(transition, admission, CancellationToken.None);

            UiData.GenerationRuntimePreparationResult result = await transition.ExecuteCandidateAsync<UiData.AppDataGenerationRuntime, UiData.GenerationRuntimePreparationResult>(
                (runtime, _, token) => runtime.PrepareReplacementAdmittedAsync(admission, token), CancellationToken.None);

            Assert.True(result.IsSucceeded, result.Code);
            Assert.False(result.RequiresRestart);
            var candidate = Assert.IsType<UiData.AppDataGenerationRuntime>(fixture.Containers[^1].GetService(typeof(UiData.AppDataGenerationRuntime)));
            Assert.False(candidate.IsExecutionPublished);
            Assert.True(candidate.TriggerSettings.Scheduler.IsRunning);
            Assert.False((await candidate.Subscriptions.QuiesceAsync(CancellationToken.None)).WasRunning);
            Assert.Empty(candidate.Repositories.Session.Snapshot.PendingApplications);
            Assert.Equal(10000, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
            Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
            await Assert.ThrowsAsync<DataGenerationManagerException>(() => candidate.PublishCommittedAsync(CancellationToken.None));

            _ = await transition.PromoteManifestAsync(directory.Store, CancellationToken.None);
            transition.SwapToPromoted();
            await transition.CommitAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => candidate.PublishCommittedAsync(CancellationToken.None));
            Assert.False(candidate.IsExecutionPublished);
            await admission.DisposeAsync();
            await candidate.PublishCommittedAsync(CancellationToken.None);
            Assert.True(candidate.IsExecutionPublished);
            QuiescedState publishedSubscriptions = await candidate.Subscriptions.QuiesceAsync(CancellationToken.None);
            Assert.True(publishedSubscriptions.WasRunning);
            await candidate.Subscriptions.ResumeAsync(publishedSubscriptions, CancellationToken.None);
            await candidate.PublishCommittedAsync(CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => original.PublishCommittedAsync(CancellationToken.None));
            await using (MutationAdmissionLease repeatedStartup = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None))
            {
                long revision = candidate.Repositories.Session.Snapshot.EnvelopeRevision;
                Assert.Equal(StartupStepOutcome.Succeeded, (await candidate.InitializeAdmittedAsync(repeatedStartup, CancellationToken.None)).Outcome);
                Assert.Equal(revision, candidate.Repositories.Session.Snapshot.EnvelopeRevision);
            }
        }
        finally
        {
            if (!transition.IsCommitted)
            {
                if (transition.IsManifestPromoted) { _ = await transition.RestoreBaselineAsync(directory.Store, CancellationToken.None); }
                else { await transition.AbortAsync(directory.Store, CancellationToken.None); }
            }
        }
    }

    [Fact]
    public async Task FailedReplacementPreparation_KeepsExecutionHeldAndAbortStopsEveryCandidateProducer()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface { Unknown = true });
        var baseline = await fixture.StartAsync();
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        _ = await fixture.CreateCandidatePreparer().StageResetAdmittedAsync(transition, admission, CancellationToken.None);
        var candidate = Assert.IsType<UiData.AppDataGenerationRuntime>(fixture.Containers[^1].GetService(typeof(UiData.AppDataGenerationRuntime)));

        UiData.GenerationRuntimePreparationResult result = await transition.ExecuteCandidateAsync<UiData.AppDataGenerationRuntime, UiData.GenerationRuntimePreparationResult>(
            (runtime, _, token) => runtime.PrepareReplacementAdmittedAsync(admission, token), CancellationToken.None);

        Assert.False(result.IsSucceeded);
        Assert.False(candidate.IsExecutionPublished);
        Assert.False((await candidate.Subscriptions.QuiesceAsync(CancellationToken.None)).WasRunning);
        await transition.AbortAsync(directory.Store, CancellationToken.None);
        Assert.False(candidate.TriggerSettings.Scheduler.IsRunning);
        Assert.False(candidate.Sampling.IsRunning);
        await admission.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => candidate.PublishCommittedAsync(CancellationToken.None));
        Assert.Equal(baseline.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
    }
}

extern alias ClashSharpUi;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using ClashSharp.Settings;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task ImportCoordinator_UsesTheValidatedSnapshotEvenIfTheSelectedPackageChangesLater()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        fixture.LegacyValues[SettingsRegistry.Keys.NotificationEnabled.Value] = false;
        NetworkSurface network = new();
        UiService.AppSettingsService settings = await StartReplacementFixtureAsync(fixture, network);
        XDocument package = EmptyPackage();
        package.Root!.Element("Settings")!.Add(new XElement("Setting", new XAttribute("Name", "MixedPort"), new XAttribute("Value", "34567")));
        string input = Path.Combine(directory.RootPath, "import.xml");
        await File.WriteAllTextAsync(input, package.ToString());
        bool changed = false;
        network.BeforeApply = (_, _) =>
        {
            if (!changed) { changed = true; File.WriteAllText(input, "changed after preflight"); }
        };

        var result = await fixture.CreateReplacementCoordinator().ImportAsync(input, CancellationToken.None);

        Assert.True(changed);
        Assert.Empty(result.Warnings);
        Assert.Equal(34567, settings.MixedPort);
        Assert.False(settings.NotificationEnabled);
        Assert.Equal(34567, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
    }

    [Fact]
    public async Task CancellationAfterNativeRelease_DoesNotAbandonTheAcceptedReplacement()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        _ = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        using CancellationTokenSource cancellation = new();
        network.BeforeApply = (_, token) => { cancellation.Cancel(); Assert.False(token.IsCancellationRequested); };

        var result = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.NotEqual(before.Descriptor.GenerationId, result.Generation.GenerationId);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
    }

    [Fact]
    public async Task NotificationFailureAfterCommit_ReturnsAWarningWithoutRevertingTheVerifiedGeneration()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        UiService.AppSettingsService settings = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        fixture.Authority.StateChanged += _ => throw new IOException("view unavailable");

        var result = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);

        Assert.NotEqual(before.Descriptor.GenerationId, result.Generation.GenerationId);
        Assert.Contains("data.replacement.notification_failed", result.Warnings);
        Assert.True(result.RequiresRestart);
        Assert.Equal(10000, settings.MixedPort);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task CleanupFailureAfterCommit_RetainsForwardDecisionAndBlocksFurtherMutations()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        Producer blocked = new();
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        var compose = fixture.ComposeRuntime!;
        fixture.ComposeRuntime = async (repositories, token) =>
        {
            await compose(repositories, token);
            if (repositories.Generation.GenerationNumber == 1) { repositories.OwnProducer(blocked); }
        };
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(fixture.Manager), new UiService.AppSettingsService(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        var before = fixture.Manager.CurrentManifest;
        try
        {
            var failure = await Assert.ThrowsAsync<UiData.GenerationReplacementRecoveryException>(() =>
                fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None));
            Assert.True(failure.IsCommitted);
            Assert.NotEqual(before.Descriptor.GenerationId, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.Descriptor.GenerationId);
            Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Admission.State);
            Assert.Throws<MutationAdmissionRejectedException>(() => fixture.Admission.AcquireOrdinary());
        }
        finally { blocked.FailStop = false; }
    }

    [Fact]
    public async Task FullReplacementCoordinator_CommitsResetPublishesSettingsAndStartsOnlyTheNewRuntime()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        fixture.LegacyValues[SettingsRegistry.Keys.NotificationEnabled.Value] = false;
        NetworkSurface network = new();
        UiService.AppSettingsService settings = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        UiData.AppDataGenerationRuntime original = RuntimeOf(fixture);
        List<bool> notified = [];
        settings.SettingChanged += (_, change) => { if (change.Key == "NotificationEnabled") { notified.Add(settings.NotificationEnabled); } };

        UiData.GenerationReplacementResult result = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);

        Assert.NotEqual(before.Descriptor.GenerationId, result.Generation.GenerationId);
        Assert.True(result.Generation.IsSameGeneration(fixture.Manager.CurrentManifest.Descriptor));
        Assert.Equal(10000, settings.MixedPort);
        Assert.True(settings.NotificationEnabled);
        Assert.Contains(true, notified);
        Assert.Empty(result.Warnings);
        Assert.False(result.RequiresRestart);
        Assert.Equal(10000, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
        Assert.False(original.Sampling.IsRunning);
        Assert.False(original.TriggerSettings.Scheduler.IsRunning);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.True(File.Exists(Path.Combine(before.Descriptor.RootPath, "ProfileCatalog.json")));
    }

    [Fact]
    public async Task InvalidImport_FailsBeforePausingOrChangingTheLiveRuntime()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        _ = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        int nativeChanges = 0;
        network.BeforeApply = (_, _) => ++nativeChanges;
        string input = Path.Combine(directory.RootPath, "invalid-import.xml");
        await File.WriteAllTextAsync(input, "not a data package");

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.CreateReplacementCoordinator().ImportAsync(input, CancellationToken.None)));

        Assert.Equal(0, nativeChanges);
        Assert.Single(fixture.Containers);
        Assert.Equal(before.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
        Assert.True(RuntimeOf(fixture).Sampling.IsRunning);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task RejectedCandidate_RestoresObservedNativeBaselineAndResumesOriginalProducers()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        NetworkSurface network = new();
        UiService.AppSettingsService settings = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        var actual = new ClashSharpUi::ClashSharp.Hosting.Settings.NetworkSettingsConfiguration(ClashSharpMode.Disabled, "observed-profile", false, 25001);
        await network.ApplyConfigurationAsync(actual, CancellationToken.None);
        network.BeforeApply = (target, _) => { if (target.MixedPort == 10000) { throw new IOException("candidate rejected"); } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None));

        Assert.Equal(before.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
        Assert.Equal(actual, await network.ReadConfigurationAsync(CancellationToken.None));
        Assert.Equal(23456, settings.MixedPort);
        Assert.True(RuntimeOf(fixture.Containers[0]).IsExecutionPublished);
        Assert.True(RuntimeOf(fixture.Containers[0]).Sampling.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[1].Session.Snapshot);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task FailedCompensation_KeepsOrdinaryChangesClosedAndRetainsUnresolvedGenerationOwnership()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        fixture.LegacyValues[SettingsRegistry.Keys.CurrentMode.Value] = (int)ClashSharpMode.RuleTakeover;
        NetworkSurface network = new();
        _ = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        network.BeforeApply = (target, _) =>
        {
            if (target.MixedPort == 10000 || target.Mode == ClashSharpMode.RuleTakeover) { throw new IOException("native state cannot be restored"); }
        };

        UiData.GenerationReplacementRecoveryException failed = await Assert.ThrowsAsync<UiData.GenerationReplacementRecoveryException>(
            () => fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None));

        Assert.False(failed.IsCommitted);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Admission.State);
        Assert.Throws<MutationAdmissionRejectedException>(() => fixture.Admission.AcquireOrdinary());
        Assert.Throws<DataGenerationManagerException>(() => new UiData.GenerationLogStorage(fixture.Manager).GetRecentLogs(1));
        Assert.Equal(before.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        Assert.NotEmpty(fixture.Containers[0].Session.Snapshot.Desired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestFailure_IsClassifiedByDurablePointerBeforeRollbackOrForwardCompletion(bool afterPromotion)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        NetworkSurface network = new();
        _ = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        FailingPromotionStore store = new(directory.Store, afterPromotion);
        var coordinator = fixture.CreateReplacementCoordinator(store);
        if (afterPromotion)
        {
            var result = await coordinator.ResetAllSettingsAsync(CancellationToken.None);
            Assert.Contains("data.replacement.promotion_reply_lost", result.Warnings);
            Assert.False(result.RequiresRestart);
            Assert.NotEqual(before.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
            Assert.Equal(10000, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => coordinator.ResetAllSettingsAsync(CancellationToken.None));
            Assert.Equal(before.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
            Assert.Equal(23456, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
        }
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.Equal(1, store.Promotions);
    }

    private static async Task<UiService.AppSettingsService> StartReplacementFixtureAsync(Fixture fixture, NetworkSurface network)
    {
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(fixture.Manager), settings).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        await RuntimeOf(fixture).Subscriptions.StartAsync(CancellationToken.None);
        return settings;
    }

    private static UiData.AppDataGenerationRuntime RuntimeOf(UiData.AppDataGenerationRepositories repositories) =>
        Assert.IsType<UiData.AppDataGenerationRuntime>(repositories.GetService(typeof(UiData.AppDataGenerationRuntime)));

    private sealed partial class Fixture
    {
        public UiData.GenerationReplacementCoordinator CreateReplacementCoordinator(IDataGenerationStore? store = null) =>
            new(Manager, Admission, store ?? _directory.Store, CreateCandidatePreparer(), Authority);
    }

    private sealed class FailingPromotionStore(IDataGenerationStore inner, bool afterPromotion) : IDataGenerationStore
    {
        public int Promotions { get; private set; }
        public Task<DataGenerationManifestSnapshot?> LoadCurrentAsync(CancellationToken token) => inner.LoadCurrentAsync(token);
        public async Task<DataGenerationManifestSnapshot> PromoteAsync(DataGenerationDescriptor descriptor, string? expectedHash, CancellationToken token)
        {
            ++Promotions;
            if (afterPromotion) { _ = await inner.PromoteAsync(descriptor, expectedHash, token); }
            throw new IOException("promotion reply unavailable");
        }
        public Task<DataGenerationManifestSnapshot> RestoreAsync(DataGenerationManifestSnapshot baseline, string expectedHash, CancellationToken token) => inner.RestoreAsync(baseline, expectedHash, token);
    }
}

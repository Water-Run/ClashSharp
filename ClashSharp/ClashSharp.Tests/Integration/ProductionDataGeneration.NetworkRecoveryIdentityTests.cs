extern alias ClashSharpUi;
using System.Runtime.CompilerServices;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Model;
using ClashSharp.Settings;
using UiCompatibility = ClashSharpUi::ClashSharp.Hosting.Compatibility;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;
using UiStartup = ClashSharpUi::ClashSharp.Hosting.Startup;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task NetworkDataIdentity_FirstMigrationStillWaitsForLegacyRecovery()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings);
        UiStartup.GenerationRecoveryDataStartupStep step = new(directory.Store, data);

        Assert.Equal(StartupStepOutcome.Succeeded, (await step.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);

        Assert.Empty(fixture.Containers);
        Assert.Null(settings.GetBoundDataGenerationId());
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(new DataGenerationPathPolicy(directory.RootPath).GenerationsRootPath));
        Assert.InRange(step.Order, 141, 144);
    }

    [Fact]
    public async Task NetworkDataIdentity_ExistingDataOpensBeforeRecoveryWithoutActivatingAnyRuntime()
    {
        await using DataGenerationTestDirectory directory = new();
        await using (Fixture initial = new(directory))
        {
            initial.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
            initial.LegacyValues[SettingsRegistry.Keys.CurrentMode.Value] = (int)ClashSharpMode.RuleTakeover;
            _ = await initial.StartAsync();
        }
        await using Fixture fixture = new(directory);
        NetworkSurface network = new() { BeforeApply = (_, _) => throw new InvalidOperationException("Recovery bootstrap must not activate network.") };
        StartupPlatform startup = new();
        AppearanceSurface appearance = new() { BeforeApply = _ => throw new InvalidOperationException("Recovery bootstrap must not apply appearance.") };
        ConfigureRealRuntime(fixture, startup, appearance, network);
        UiService.AppSettingsService settings = new(new Dictionary<string, object> { ["MixedPort"] = 34567 });
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings);
        var step = new UiStartup.GenerationRecoveryDataStartupStep(directory.Store, data);

        Assert.Equal(StartupStepOutcome.Succeeded, (await step.ExecuteAsync(new AppLaunchRequest("--startup-restore"), CancellationToken.None)).Outcome);

        Assert.Equal(fixture.Manager.CurrentManifest.Descriptor.GenerationId, settings.GetBoundDataGenerationId());
        Assert.Equal(23456, settings.MixedPort);
        Assert.Equal(ClashSharpMode.RuleTakeover, settings.CurrentMode);
        Assert.False(RuntimeOf(fixture).IsExecutionPublished);
        Assert.False(RuntimeOf(fixture).Sampling.IsRunning);
        Assert.False(RuntimeOf(fixture).TriggerSettings.Scheduler.IsRunning);
    }

    [Fact]
    public async Task LoginRecovery_OpensTheExistingGenerationWithNoWindowAndPreservesSettingsAndProducerState()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot original;
        byte[] savedSettings;
        await using (Fixture initial = new(directory))
        {
            initial.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
            initial.LegacyValues[SettingsRegistry.Keys.CurrentMode.Value] = (int)ClashSharpMode.RuleTakeover;
            original = await initial.StartAsync();
            savedSettings = File.ReadAllBytes(Path.Combine(original.Descriptor.RootPath, "Settings", "v1", "settings-envelope.json"));
        }
        await using Fixture fixture = new(directory);
        var presentation = UiData.GenerationRuntimePresentation.Select(true,
            () => throw new InvalidOperationException("The generation startup window is unavailable."), () => false);
        ConfigureRealRuntime(fixture, new StartupPlatform(), presentation.Appearance,
            new NetworkSurface { BeforeApply = (_, _) => throw new InvalidOperationException("Recovery must not activate network.") },
            presentation.CreateDispatcher);
        UiService.AppSettingsService settings = new(new Dictionary<string, object> { ["MixedPort"] = 34567 });
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings);
        var step = new UiStartup.GenerationRecoveryDataStartupStep(directory.Store, data);

        var result = await step.ExecuteAsync(new AppLaunchRequest(UiService.StartupRestoreFallbackService.HelperArgument), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        Assert.Equal(original.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        Assert.Equal(original.Descriptor.GenerationId, settings.GetBoundDataGenerationId());
        Assert.Equal(23456, settings.MixedPort);
        Assert.Equal(ClashSharpMode.RuleTakeover, settings.CurrentMode);
        Assert.Equal(savedSettings, File.ReadAllBytes(Path.Combine(original.Descriptor.RootPath, "Settings", "v1", "settings-envelope.json")));
        Assert.False(RuntimeOf(fixture).IsExecutionPublished);
        Assert.False(RuntimeOf(fixture).Sampling.IsRunning);
        Assert.False(RuntimeOf(fixture).TriggerSettings.Scheduler.IsRunning);
    }

    [Fact]
    public async Task NetworkDataIdentity_MissingPointerWithReplacementRecoveryCannotEnterTheLegacyHelper()
    {
        await using DataGenerationTestDirectory directory = new();
        await using (Fixture initial = new(directory))
        {
            _ = await StartReplacementFixtureAsync(initial, new NetworkSurface());
            await using var lease = await initial.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            var external = await RuntimeOf(initial).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);
            await new UiData.FileGenerationReplacementJournal(directory.RootPath).BeginAsync(
                Guid.NewGuid(), initial.Manager.CurrentManifest, external, CancellationToken.None);
        }
        File.Delete(new DataGenerationPathPolicy(directory.RootPath).CurrentManifestPath);
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings);

        var result = await new UiStartup.GenerationRecoveryDataStartupStep(directory.Store, data).ExecuteAsync(
            new AppLaunchRequest(UiService.StartupRestoreFallbackService.HelperArgument), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        Assert.Empty(fixture.Containers);
        Assert.Null(settings.GetBoundDataGenerationId());
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Admission.State);
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkDataIdentity_ChangedRecoveryPointerCannotAllocateOrOpenAnotherDirectory(bool removePointer)
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot original;
        await using (Fixture initial = new(directory)) { original = await initial.StartAsync(); }
        DataGenerationDescriptor alternative = directory.CreateGeneration(2);
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings);
        var racingStore = new RecoveryPointerRaceStore(directory.Store, async () =>
        {
            if (removePointer) { File.Delete(new DataGenerationPathPolicy(directory.RootPath).CurrentManifestPath); }
            else { _ = await directory.Store.PromoteAsync(alternative, original.ContentHash, CancellationToken.None); }
        });

        var result = await new UiStartup.GenerationRecoveryDataStartupStep(racingStore, data).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        Assert.Empty(fixture.Containers);
        Assert.Null(settings.GetBoundDataGenerationId());
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Admission.State);
        Assert.Equal(2, Directory.GetDirectories(new DataGenerationPathPolicy(directory.RootPath).GenerationsRootPath).Length);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("stage")]
    [InlineData("apply")]
    [InlineData("probe")]
    [InlineData("compensate")]
    [InlineData("activate")]
    [InlineData("cleanup")]
    public async Task NetworkDataIdentity_StalePlanIsRejectedBeforeEveryNativeAdapterPhase(string phase)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        settings.BindAuthority(fixture.Authority);
        var adapter = CreateIdentityTripwireAdapter(settings);
        NetworkPlan plan = IdentityPlan(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => phase switch
        {
            "validate" => adapter.ValidateAsync(plan, CancellationToken.None),
            "stage" => adapter.StageAsync(plan, CancellationToken.None),
            "apply" => adapter.ApplyAsync(plan, CancellationToken.None),
            "probe" => adapter.ProbeAsync(plan, CancellationToken.None),
            "compensate" => adapter.CompensateAsync(plan, CancellationToken.None),
            "activate" => adapter.ActivateAsync(plan, CancellationToken.None),
            _ => adapter.CleanupAsync(plan, CancellationToken.None),
        });
    }

    [Fact]
    public async Task NetworkDataIdentity_ManagedModeChangesCannotEnterTheLegacyNativeOrSettingsPath()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        Dictionary<string, object> legacy = new() { ["MixedPort"] = 23456 };
        UiService.AppSettingsService settings = new(legacy);
        settings.BindAuthority(fixture.Authority);
        var adapter = CreateIdentityTripwireAdapter(settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.PlanAsync(
            NetworkIntent.ChangeMode(ClashSharpMode.RuleTakeover, false, 10000), CancellationToken.None));
        var committer = new UiCompatibility.LegacyNetworkStateCommitter(settings);
        using var lease = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => committer.PromoteDesiredAsync(IdentityPlan(Guid.NewGuid()), lease, CancellationToken.None));
        Assert.Equal(23456, legacy["MixedPort"]);
    }

    private static UiCompatibility.LegacyNetworkStateAdapter CreateIdentityTripwireAdapter(UiService.AppSettingsService settings) =>
        new(settings, IdentityUnused<UiService.NetworkTakeoverService>(), IdentityUnused<UiService.WindowsProxyService>(),
            IdentityUnused<UiService.MihomoCoreService>(), IdentityUnused<UiService.CoreConfigurationService>(),
            IdentityUnused<UiService.MihomoServiceManager>(), IdentityUnused<UiService.ProxyRecoveryService>());

    private static T IdentityUnused<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static NetworkPlan IdentityPlan(Guid? id)
    {
        NetworkIntent intent = NetworkIntent.Shutdown(ClashSharpMode.Disabled, false, 10000);
        NetworkStateSnapshot state = new(ClashSharpMode.Disabled, false, false, false, 10000, "state");
        string payload = UiCompatibility.LegacyNetworkPlanPersistence.Serialize(intent, state, state, "baseline", "desired",
            string.Empty, string.Empty, ClashSharpMode.RuleTakeover, false, 10000, id);
        return new(intent, state, state, "baseline", "desired", payload);
    }

    private sealed class RecoveryPointerRaceStore(IDataGenerationStore inner, Func<Task> change) : IDataGenerationStore
    {
        public async Task<DataGenerationManifestSnapshot?> LoadCurrentAsync(CancellationToken token)
        {
            var snapshot = await inner.LoadCurrentAsync(token);
            await change();
            return snapshot;
        }
        public Task<DataGenerationManifestSnapshot> PromoteAsync(DataGenerationDescriptor descriptor, string? expectedHash, CancellationToken token) =>
            throw new InvalidOperationException("Recovery lookup cannot promote data.");
        public Task<DataGenerationManifestSnapshot> RestoreAsync(DataGenerationManifestSnapshot baseline, string expectedHash, CancellationToken token) =>
            throw new InvalidOperationException("Recovery lookup cannot restore data.");
    }
}

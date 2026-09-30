extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;
using NetworkConfiguration = ClashSharpUi::ClashSharp.Hosting.Settings.NetworkSettingsConfiguration;
using StartupState = ClashSharpUi::ClashSharp.Service.StartupLaunchTaskState;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task ExternalRecovery_CapturesActualStateAndRestoresItWithoutChangingDesiredPreferences()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        StartupPlatform startup = new();
        AppearanceSurface appearance = new();
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, startup, appearance, network);
        _ = await fixture.StartAsync();
        UiData.AppDataGenerationRuntime runtime = RuntimeOf(fixture);
        SettingsEnvelope preferences = runtime.Repositories.Session.Snapshot;
        startup.State = StartupState.Enabled;
        appearance.ApplyTheme(AppThemeMode.Dark);
        NetworkConfiguration actual = new(ClashSharpMode.RuleTakeover, "baseline-profile", false, 23456);
        await network.ApplyConfigurationAsync(actual, CancellationToken.None);
        await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);

        UiData.GenerationExternalStateSnapshot snapshot = await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);

        Assert.True(snapshot.StartupEnabled);
        Assert.Equal(AppThemeMode.Dark, snapshot.Appearance.Theme);
        Assert.Equal(actual, snapshot.Network);
        Assert.False(preferences.Desired[SettingsRegistry.Keys.LaunchAtStartupEnabled].Value.Get<bool>());
        Assert.NotEqual(snapshot.Appearance.Theme, preferences.Desired[SettingsRegistry.Keys.AppThemeMode].Value.Get<AppThemeMode>());
        startup.State = StartupState.Disabled;
        appearance.ApplyTheme(AppThemeMode.Light);
        await network.ApplyConfigurationAsync(new(ClashSharpMode.Disabled, "candidate-profile", true, 34567), CancellationToken.None);
        await runtime.ExternalState.RestoreAdmittedAsync(snapshot, lease, CancellationToken.None);
        Assert.Equal(snapshot, await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None));
        Assert.Same(preferences, runtime.Repositories.Session.Snapshot);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("appearance")]
    public async Task PartialExternalRecovery_ReportsFailureAndStillAttemptsTheRemainingCategories(string failing)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        StartupPlatform startup = new() { State = StartupState.Enabled };
        AppearanceSurface appearance = new();
        appearance.ApplyTheme(AppThemeMode.Dark);
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, startup, appearance, network);
        _ = await fixture.StartAsync();
        UiData.AppDataGenerationRuntime runtime = RuntimeOf(fixture);
        await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        var snapshot = await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);
        startup.State = StartupState.Disabled;
        appearance.ApplyTheme(AppThemeMode.Light);
        await network.ApplyConfigurationAsync(new(ClashSharpMode.Disabled, "candidate-profile", false, 23456), CancellationToken.None);
        List<string> attempted = [];
        network.BeforeApply = (_, _) => { attempted.Add("network"); if (failing == "network") { throw new IOException("network restore failed"); } };
        appearance.BeforeApply = part => { attempted.Add(part); if (failing == "appearance" && part == "theme") { throw new IOException("theme restore failed"); } };

        await Assert.ThrowsAsync<AggregateException>(() => runtime.ExternalState.RestoreAdmittedAsync(snapshot, lease, CancellationToken.None));

        Assert.Equal(StartupState.Enabled, startup.State);
        Assert.Equal(["network", "language", "theme", "accent"], attempted);
        Assert.Equal(failing == "appearance" ? AppThemeMode.Light : AppThemeMode.Dark, appearance.CaptureConfiguration().Theme);
        if (failing == "appearance") { Assert.Equal(snapshot.Network, await network.ReadConfigurationAsync(CancellationToken.None)); }
    }

    [Fact]
    public async Task ExternalRecovery_AcceptsLostNativeReplyOnlyAfterIndependentVerification()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        StartupPlatform startup = new();
        AppearanceSurface appearance = new();
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, startup, appearance, network);
        _ = await fixture.StartAsync();
        UiData.AppDataGenerationRuntime runtime = RuntimeOf(fixture);
        await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        var snapshot = await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);
        await network.ApplyConfigurationAsync(new(ClashSharpMode.Disabled, "candidate-profile", false, 23456), CancellationToken.None);
        network.AfterApply = () => throw new IOException("reply lost after effect");

        await runtime.ExternalState.RestoreAdmittedAsync(snapshot, lease, CancellationToken.None);

        Assert.Equal(snapshot, await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None));
    }

    [Fact]
    public async Task ExternalRecovery_FinishesAcceptedCompensationAfterCallerCancellation()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        StartupPlatform startup = new() { State = StartupState.Enabled };
        AppearanceSurface appearance = new();
        appearance.ApplyTheme(AppThemeMode.Dark);
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, startup, appearance, network);
        _ = await fixture.StartAsync();
        UiData.AppDataGenerationRuntime runtime = RuntimeOf(fixture);
        await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        var snapshot = await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);
        startup.State = StartupState.Disabled;
        appearance.ApplyTheme(AppThemeMode.Light);
        using CancellationTokenSource cancellation = new();
        network.BeforeApply = (_, token) => { cancellation.Cancel(); Assert.False(token.IsCancellationRequested); };

        await runtime.ExternalState.RestoreAdmittedAsync(snapshot, lease, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(snapshot, await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None));
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("network")]
    public async Task ExternalCapture_RejectsUnknownNativeStateInsteadOfUsingPreferences(string unknown)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        StartupPlatform startup = new() { State = unknown == "startup" ? StartupState.Other : StartupState.Disabled };
        AppearanceSurface appearance = new();
        NetworkSurface network = new() { Unknown = unknown == "network" };
        ConfigureRealRuntime(fixture, startup, appearance, network);
        _ = await fixture.StartAsync();
        UiData.AppDataGenerationRuntime runtime = RuntimeOf(fixture);
        await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        Assert.NotNull(await Record.ExceptionAsync(() => runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None)));
    }

    [Fact]
    public async Task ExternalRecovery_RejectsForeignSnapshotBeforeStartingEffects()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        StartupPlatform startup = new();
        AppearanceSurface appearance = new();
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, startup, appearance, network);
        _ = await fixture.StartAsync();
        UiData.AppDataGenerationRuntime runtime = RuntimeOf(fixture);
        await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        var snapshot = await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);
        appearance.BeforeApply = _ => throw new InvalidOperationException("effects must not start");
        network.BeforeApply = (_, _) => throw new InvalidOperationException("effects must not start");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExternalState.RestoreAdmittedAsync(
            snapshot with { Generation = directory.CreateGeneration(2) }, lease, CancellationToken.None));
        Assert.Equal(StartupState.Disabled, startup.State);
        await fixture.Manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.ExternalState.RestoreAdmittedAsync(snapshot, lease, CancellationToken.None));
    }

    private static UiData.AppDataGenerationRuntime RuntimeOf(Fixture fixture) =>
        Assert.IsType<UiData.AppDataGenerationRuntime>(fixture.Containers[^1].GetService(typeof(UiData.AppDataGenerationRuntime)));
}

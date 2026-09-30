extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;
using UiAdapter = ClashSharpUi::ClashSharp.Presentation.Adapters;
using UiService = ClashSharpUi::ClashSharp.Service;
using UiView = ClashSharpUi::ClashSharp.ViewModel;

namespace ClashSharp.Tests.Integration;

public sealed partial class GenerationSettingsAuthorityTests
{
    [Theory]
    [InlineData(SettingsResetScope.Startup, false)]
    [InlineData(SettingsResetScope.Startup, true)]
    [InlineData(SettingsResetScope.Proxy, false)]
    [InlineData(SettingsResetScope.Proxy, true)]
    [InlineData(SettingsResetScope.TransparentProxy, false)]
    [InlineData(SettingsResetScope.TransparentProxy, true)]
    public async Task RuntimeGroupReset_AppliesOnlySelectedDefaultsAndPreservesOtherDesiredValues(SettingsResetScope scope, bool tunSupported)
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        SettingsEnvelope before = fixture.Session.Snapshot;

        SettingsRuntimeGroupResetResult result = await fixture.Authority.ResetRuntimeGroupAsync(scope, tunSupported, CancellationToken.None);

        Assert.True(result.Outcome.IsSucceeded, result.Outcome.Code);
        Assert.False(result.CanRetry);
        Assert.True(result.Generation.IsSameGeneration(fixture.Manifest.Descriptor));
        var definitions = SettingsRegistry.Default.GetResetDefinitions(scope).ToDictionary(definition => definition.Key);
        foreach (var (key, prior) in before.Desired)
        {
            if (definitions.TryGetValue(key, out var definition))
            {
                var expected = key == SettingsRegistry.Keys.TransparentProxyEnabled && !tunSupported
                    ? definition.NormalizeValue(false).Value! : definition.DefaultValue;
                Assert.Equal(expected, result.Outcome.Envelope!.Desired[key].Value);
                Assert.Equal(expected, result.Outcome.Envelope.Applied[key].Value);
                Assert.Equal(SettingAppliedStateKind.Verified, result.Outcome.Envelope.Applied[key].Kind);
            }
            else { Assert.Equal(prior, result.Outcome.Envelope!.Desired[key]); }
        }
        Assert.Equal(Hash(result.Outcome.Envelope!), Hash((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!));
    }

    [Fact]
    public async Task RuntimeGroupReset_UnchangedDefaultRepairsObservedStartupDrift()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        var before = fixture.Session.Snapshot.Desired[SettingsRegistry.Keys.LaunchAtStartupEnabled];
        Participant startup = fixture.Participants[SettingApplicationKind.StartupTask];
        startup.SetObserved(SettingsRegistry.Keys.LaunchAtStartupEnabled, Change("LaunchAtStartupEnabled", "true").Value);

        var result = await fixture.Authority.ResetRuntimeGroupAsync(SettingsResetScope.Startup, true, CancellationToken.None);

        Assert.True(result.Outcome.IsSucceeded, result.Outcome.Code);
        Assert.Equal(before, result.Outcome.Envelope!.Desired[SettingsRegistry.Keys.LaunchAtStartupEnabled]);
        Assert.False(startup.GetObserved(SettingsRegistry.Keys.LaunchAtStartupEnabled).Get<bool>());
        Assert.Equal(1, startup.Applies);
    }

    [Fact]
    public async Task RuntimeGroupReset_ExplicitRetryPreservesUnrelatedFailedIntent()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        fixture.Participants[SettingApplicationKind.Internal].IgnoreEffects = true;
        var unrelated = await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "true")], Guid.NewGuid(), CancellationToken.None);
        var unrelatedBatch = Assert.Single(unrelated.Envelope!.PendingApplications);
        fixture.Participants[SettingApplicationKind.Internal].IgnoreEffects = false;
        fixture.Participants[SettingApplicationKind.Network].IgnoreEffects = true;
        var failed = await fixture.Authority.ResetRuntimeGroupAsync(SettingsResetScope.Proxy, true, CancellationToken.None);
        Assert.True(failed.CanRetry);
        Assert.Equal(10000, failed.Outcome.Envelope!.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        Assert.Equal(23456, fixture.Participants[SettingApplicationKind.Network].GetObserved(SettingsRegistry.Keys.MixedPort).Get<int>());

        fixture.Participants[SettingApplicationKind.Network].IgnoreEffects = false;
        var retried = await fixture.Authority.RetryRuntimeGroupAsync(failed, CancellationToken.None);

        Assert.True(retried.Outcome.IsSucceeded, retried.Outcome.Code);
        var remaining = Assert.Single(retried.Outcome.Envelope!.PendingApplications);
        Assert.Equal(unrelatedBatch.BatchId, remaining.BatchId);
        Assert.Equal(unrelatedBatch.AttemptId, remaining.AttemptId);
        Assert.Equal(SettingsApplicationBatchState.Failed, remaining.State);
        Assert.False(fixture.Participants[SettingApplicationKind.Internal].GetObserved(SettingsRegistry.Keys.NotificationEnabled).Get<bool>());
        Assert.Equal(10000, fixture.Participants[SettingApplicationKind.Network].GetObserved(SettingsRegistry.Keys.MixedPort).Get<int>());
    }

    [Fact]
    public async Task RuntimeGroupReset_RejectsRetryAfterAnotherAttemptWithoutApplyingAnything()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        Participant network = fixture.Participants[SettingApplicationKind.Network];
        network.IgnoreEffects = true;
        var failed = await fixture.Authority.ResetRuntimeGroupAsync(SettingsResetScope.Proxy, true, CancellationToken.None);
        var pending = failed.Outcome.Envelope!.PendingApplications.Single(batch => batch.State == SettingsApplicationBatchState.Failed);
        _ = await fixture.Authority.RetryAsync(pending.BatchId, pending.AttemptId, Guid.NewGuid(), CancellationToken.None);
        int applications = network.Applies;

        var stale = await fixture.Authority.RetryRuntimeGroupAsync(failed, CancellationToken.None);

        Assert.Equal(SettingsAuthorityStatus.Rejected, stale.Outcome.Status);
        Assert.Equal("settings.reset.stale_retry", stale.Outcome.Code);
        Assert.False(stale.CanRetry);
        Assert.Equal(applications, network.Applies);
    }

    [Fact]
    public async Task RuntimeGroupReset_RejectsDifferentDataIdentityEvenWhenBatchAndAttemptMatch()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        await using Fixture other = await Fixture.CreateAsync();
        Participant network = fixture.Participants[SettingApplicationKind.Network];
        network.IgnoreEffects = true;
        var failed = await fixture.Authority.ResetRuntimeGroupAsync(SettingsResetScope.Proxy, true, CancellationToken.None);
        int applications = network.Applies;

        var stale = await fixture.Authority.RetryRuntimeGroupAsync(failed with { Generation = other.Manifest.Descriptor }, CancellationToken.None);

        Assert.Equal(SettingsAuthorityStatus.Rejected, stale.Outcome.Status);
        Assert.Equal("settings.reset.stale_retry", stale.Outcome.Code);
        Assert.Equal(applications, network.Applies);
    }

    [Fact]
    public async Task RuntimeGroupReset_CancellationAfterCommitStillOwnsEffectsAndBlocksDirectoryDrain()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Participants[SettingApplicationKind.Network].BeforeApply = async () =>
        {
            cancellation.Cancel();
            entered.SetResult();
            await release.Task;
        };
        Task<SettingsRuntimeGroupResetResult> reset = fixture.Authority.ResetRuntimeGroupAsync(SettingsResetScope.Proxy, true, cancellation.Token);
        Task<DataGenerationTransition>? draining = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            draining = fixture.Generations.BeginDrainAsync(fixture.Manifest.ContentHash, CancellationToken.None).AsTask();
            Assert.False(draining.IsCompleted);
            Assert.False(reset.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.True((await reset.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.IsSucceeded);
        await using DataGenerationTransition transition = await draining!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RuntimeGroupReset_ExplicitRetryFinishesEveryFailedParticipantAfterCancellation()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        Participant network = fixture.Participants[SettingApplicationKind.Network];
        Participant sampling = fixture.Participants[SettingApplicationKind.Sampling];
        network.IgnoreEffects = sampling.IgnoreEffects = true;
        _ = await fixture.Authority.ApplyRuntimeChangesAsync([Change("MixedPort", "10000")], Guid.NewGuid(), CancellationToken.None);
        _ = await fixture.Authority.ApplyRuntimeChangesAsync([Change("ConnectionSamplingEnabled", "true"), Change("ConnectionSamplingIntervalSeconds", "30")], Guid.NewGuid(), CancellationToken.None);
        var failed = await fixture.Authority.ResetRuntimeGroupAsync(SettingsResetScope.Proxy, false, CancellationToken.None);
        Assert.Equal(2, failed.Outcome.Envelope!.PendingApplications.Count(batch => batch.State == SettingsApplicationBatchState.Failed));
        network.IgnoreEffects = sampling.IgnoreEffects = false;
        using CancellationTokenSource cancellation = new();
        network.BeforeApply = () => { cancellation.Cancel(); return Task.CompletedTask; };

        var result = await fixture.Authority.RetryRuntimeGroupAsync(failed, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Outcome.IsSucceeded, result.Outcome.Code);
        Assert.Empty(result.Outcome.Envelope!.PendingApplications);
        Assert.True(sampling.GetObserved(SettingsRegistry.Keys.ConnectionSamplingEnabled).Get<bool>());
        Assert.Equal(30, sampling.GetObserved(SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds).Get<int>());
    }

    [Theory]
    [InlineData(SettingsResetScope.None)]
    [InlineData(SettingsResetScope.All)]
    [InlineData(SettingsResetScope.Startup | SettingsResetScope.Proxy)]
    public async Task RuntimeGroupReset_InvalidScopeDoesNotTouchSettings(SettingsResetScope scope)
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        string before = Hash(fixture.Session.Snapshot);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Authority.ResetRuntimeGroupAsync(scope, true, CancellationToken.None));
        Assert.Equal(before, Hash(fixture.Session.Snapshot));
    }

    [Fact]
    public async Task RuntimeGroupReset_ProductionPageShowsUnappliedDefaultsAndCompletesAnExplicitRetry()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        UiView.SettingsViewModel page = CreateRuntimeGroupPage(fixture);
        fixture.Participants[SettingApplicationKind.Network].IgnoreEffects = true;

        await Assert.ThrowsAsync<SettingsRuntimeGroupResetException>(() => page.ResetProxySettingsToDefaultsAsync(CancellationToken.None));

        Assert.Equal(10000, page.MixedPort);
        Assert.True(page.CanRetrySettingsReset);
        Assert.Equal("Settings.Reset.Unapplied", page.OperationErrorText);
        Assert.False(page.IsResetRecoveryRequired);
        fixture.Participants[SettingApplicationKind.Network].IgnoreEffects = false;
        await page.RetryRuntimeSettingsResetAsync(CancellationToken.None);
        Assert.False(page.CanRetrySettingsReset);
        Assert.False(page.HasOperationError);
        Assert.Equal(10000, page.MixedPort);
    }

    [Fact]
    public async Task RuntimeGroupReset_ProductionPagePublishesWholeGroupAfterEffectsAndClearsRetryOnImport()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        UiView.SettingsViewModel page = CreateRuntimeGroupPage(fixture);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Participant network = fixture.Participants[SettingApplicationKind.Network];
        network.BeforeApply = async () => { entered.SetResult(); await release.Task; };
        Task reset = page.ResetProxySettingsToDefaultsAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(23456, page.MixedPort);
            Assert.False(reset.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000, page.MixedPort);
        Assert.Equal(30, page.ConnectionSamplingIntervalSeconds);
        network.BeforeApply = null;
        network.SetObserved(SettingsRegistry.Keys.MixedPort, Change("MixedPort", "23456").Value);
        network.IgnoreEffects = true;
        await Assert.ThrowsAsync<SettingsRuntimeGroupResetException>(() => page.ResetProxySettingsToDefaultsAsync(CancellationToken.None));
        Assert.True(page.CanRetrySettingsReset);
        page.ReloadAfterDataImport();
        Assert.False(page.CanRetrySettingsReset);
        Assert.False(page.HasOperationError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeGroupReset_ObserverFailureCannotHideTheVerifiedCommandOutcome(bool applicationFails)
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        fixture.Participants[SettingApplicationKind.Network].IgnoreEffects = applicationFails;
        IOException observerFailure = new("view notification unavailable");
        fixture.Authority.StateChanged += _ => throw observerFailure;

        var result = await fixture.Authority.ResetRuntimeGroupAsync(SettingsResetScope.Proxy, true, CancellationToken.None);

        Assert.Equal(!applicationFails, result.Outcome.IsSucceeded);
        Assert.Equal(applicationFails, result.CanRetry);
        Assert.True(result.RequiresRestart);
        Assert.Same(observerFailure, result.NotificationFailure);
        Assert.Equal(Hash(result.Outcome.Envelope!), Hash((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!));
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task RuntimeGroupReset_ProductionPageRetainsRestartAfterObserverFailureAndPageReload()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        UiService.RestartRequiredStateService restart = new();
        UiView.SettingsViewModel page = CreateRuntimeGroupPage(fixture, restart.RequireRestart);
        fixture.Authority.StateChanged += _ => throw new IOException("another page is unavailable");

        await page.ResetProxySettingsToDefaultsAsync(CancellationToken.None);

        Assert.Equal(10000, page.MixedPort);
        Assert.True(page.HasRestartRequiredSettings);
        Assert.False(page.CanRetrySettingsReset);
        restart.SetRestartPending(false);
        page.Load();
        Assert.True(restart.IsRestartPending);
        Assert.True(page.HasRestartRequiredSettings);
    }

    [Fact]
    public async Task RuntimeGroupReset_ProductionPageRemovesAStaleRetryWithoutReapplyingIt()
    {
        await using Fixture fixture = await CreateRuntimeGroupFixtureAsync();
        UiView.SettingsViewModel page = CreateRuntimeGroupPage(fixture);
        Participant network = fixture.Participants[SettingApplicationKind.Network];
        network.IgnoreEffects = true;
        await Assert.ThrowsAsync<SettingsRuntimeGroupResetException>(() => page.ResetProxySettingsToDefaultsAsync(CancellationToken.None));
        var failed = fixture.Session.Snapshot.PendingApplications.Single(batch => batch.State == SettingsApplicationBatchState.Failed);
        _ = await fixture.Authority.RetryAsync(failed.BatchId, failed.AttemptId, Guid.NewGuid(), CancellationToken.None);
        int applications = network.Applies;

        var error = await Assert.ThrowsAsync<SettingsRuntimeGroupResetException>(() => page.RetryRuntimeSettingsResetAsync(CancellationToken.None));

        Assert.Equal(SettingsAuthorityStatus.Rejected, error.Status);
        Assert.Equal("Settings.Reset.Stale", page.OperationErrorText);
        Assert.False(page.CanRetrySettingsReset);
        Assert.Equal(applications, network.Applies);
    }

    private static async Task<Fixture> CreateRuntimeGroupFixtureAsync()
    {
        Fixture fixture = await Fixture.CreateAsync();
        try
        {
            Assert.True((await fixture.Authority.ApplyChangesAsync([
                Change("LaunchAtStartupEnabled", "true"), Change("StartupConflictCheckEnabled", "false"),
                Change("ShowStartupGuideOnStartup", "false"), Change("TransparentProxyEnabled", "false"),
                Change("MixedPort", "23456"), Change("ConnectionSamplingEnabled", "false"),
                Change("ConnectionSamplingIntervalSeconds", "90"), Change("NotificationEnabled", "false"),
                Change("CurrentMode", "RuleTakeover"), Change("ActiveProfileId", "group-reset-profile")], Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private static UiView.SettingsViewModel CreateRuntimeGroupPage(Fixture fixture, Action? requireRestart = null)
    {
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        settings.BindAuthority(fixture.Authority);
        UiView.SettingsViewModel page = new(new UiAdapter.AppSettingsStore(settings),
            _ => throw new InvalidOperationException("Unexpected legacy language change."),
            _ => throw new InvalidOperationException("Unexpected legacy theme change."), () => { }, _ => { }, key => key,
            () => new("config.yaml", true, "mihomo.exe"), new GroupPageErrorSink(), () => { }, () => { },
            _ => Task.FromResult(false), _ => Task.CompletedTask, _ => Task.CompletedTask, (_, _) => Task.FromResult(204),
            applyConnectionSamplingAsync: (_, _, _) => throw new InvalidOperationException("Unexpected legacy sampling change."),
            applyLaunchAtStartupAsync: (_, _) => throw new InvalidOperationException("Unexpected legacy startup change."),
            applyNetworkSettingsAsync: (_, _, _) => throw new InvalidOperationException("Unexpected legacy network change."),
            supportedLanguages: UiService.LocalizationService.GetSupportedLanguages().ToArray(),
            replaceAllSettingsAsync: _ => throw new InvalidOperationException("Unexpected full data replacement."),
            runtimeGroupReset: fixture.Authority,
            requireSettingsRestart: requireRestart);
        page.Load();
        return page;
    }

    private sealed class GroupPageErrorSink : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected detached page error.", error.Exception);
    }
}

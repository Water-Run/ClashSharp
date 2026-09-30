extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;
using PageFailure = ClashSharpUi::ClashSharp.Presentation.Adapters.SettingsPageCommandException;
using PageStore = ClashSharpUi::ClashSharp.Presentation.Adapters.GenerationSettingsStore;

namespace ClashSharp.Tests.Integration;

public sealed partial class GenerationSettingsAuthorityTests
{
    [Fact]
    public async Task PageStore_WaitsForWholePairAndRuntimeObservationBeforeReleasingMutationAdmission()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Authority.OpenAsync(CancellationToken.None)).IsSucceeded);
        PageStore page = new(fixture.Authority);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Participants[SettingApplicationKind.Internal].BeforeApply = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        Task command = page.ApplyChangesAsync([Change("NotificationEnabled", "false"), Change("NotificationLevel", "More")], CancellationToken.None);
        Task<MutationAdmissionLease>? drain = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(command.IsCompleted);
            Assert.False(page.NotificationEnabled);
            Assert.Equal("More", page.NotificationLevel.ToString());
            drain = fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
            Assert.False(drain.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await command.WaitAsync(TimeSpan.FromSeconds(5));
        await using MutationAdmissionLease exclusive = await drain!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Session.Snapshot.PendingApplications);
        Assert.Equal("false", fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.NotificationEnabled].Value!.CanonicalText);
        Assert.Equal("More", fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.NotificationLevel].Value!.CanonicalText);
        Assert.Equal(Hash(fixture.Session.Snapshot), Hash((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!));
    }

    [Fact]
    public async Task PageStore_PreservesFailedApplicationAndExposesItsCommittedDesiredChoice()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Authority.OpenAsync(CancellationToken.None)).IsSucceeded);
        PageStore page = new(fixture.Authority);
        fixture.Participants[SettingApplicationKind.Internal].IgnoreEffects = true;

        PageFailure failure = await Assert.ThrowsAsync<PageFailure>(() => page.ApplyChangesAsync(
            [Change("NotificationEnabled", "false")], CancellationToken.None));

        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, failure.Status);
        Assert.False(page.NotificationEnabled);
        var values = page.ReadPreferenceChanges([SettingsRegistry.Keys.NotificationEnabled, SettingsRegistry.Keys.NotificationLevel]);
        Assert.False(values[0].Value.Get<bool>());
        Assert.NotEmpty(fixture.Session.Snapshot.PendingApplications);
        Assert.Equal(SettingAppliedStateKind.Unknown, fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.NotificationEnabled].Kind);
    }

    [Fact]
    public async Task PageStore_AppearanceChoiceIsVerifiedBeforeTheCommandCompletes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Authority.OpenAsync(CancellationToken.None)).IsSucceeded);
        PageStore page = new(fixture.Authority);

        await page.ApplyChangesAsync([Change("DisplayLanguage", "French")], CancellationToken.None);

        Assert.Equal("French", page.DisplayLanguage.ToString());
        Assert.Empty(fixture.Session.Snapshot.PendingApplications);
        Assert.Equal("French", fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.DisplayLanguage].Value?.CanonicalText);
        Assert.Equal(1, fixture.Participants[SettingApplicationKind.Appearance].Applies);
    }

    [Fact]
    public async Task PageStore_ResetCommitsTheCompleteGroupAndPreservesOtherPreferences()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Authority.OpenAsync(CancellationToken.None)).IsSucceeded);
        PageStore page = new(fixture.Authority);
        await page.ApplyChangesAsync([Change("AppThemeMode", "Dark"), Change("CloseBehaviorMode", "ConfirmExit"),
            Change("NotificationEnabled", "false"), Change("MixedPort", "23456")], CancellationToken.None);

        await page.ResetPreferenceGroupAsync(SettingsResetScope.Basic, CancellationToken.None);

        Assert.All(SettingsRegistry.Default.GetResetDefinitions(SettingsResetScope.Basic),
            definition => Assert.Equal(definition.DefaultValue, fixture.Session.Snapshot.Desired[definition.Key].Value));
        Assert.False(page.NotificationEnabled);
        Assert.Equal(23456, page.MixedPort);
        Assert.Equal("FollowSystem", page.AppThemeMode.ToString());
    }

    [Fact]
    public async Task PageStore_CallerCancellationAfterPublicationStillWaitsForTheCommittedCommand()
    {
        using CancellationTokenSource cancellation = new();
        PublicationHook hook = new();
        await using Fixture fixture = await Fixture.CreateAsync(hook);
        Assert.True((await fixture.Authority.OpenAsync(CancellationToken.None)).IsSucceeded);
        PageStore page = new(fixture.Authority);
        hook.AfterFirstPublication = cancellation.Cancel;

        await page.ApplyChangesAsync([Change("NotificationEnabled", "false")], cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(page.NotificationEnabled);
        Assert.Empty(fixture.Session.Snapshot.PendingApplications);
        Assert.Equal("false", fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.NotificationEnabled].Value!.CanonicalText);
    }

    [Fact]
    public async Task PageStore_DoesNotReturnDefaultsBeforeVerificationOrAfterRetirement()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        PageStore page = new(fixture.Authority);
        Assert.Throws<InvalidOperationException>(() => page.AppThemeMode);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.ApplyChangesAsync(
            [Change("NotificationEnabled", "false")], cancellation.Token));
        Assert.True((await fixture.Authority.OpenAsync(CancellationToken.None)).IsSucceeded);
        Assert.True(page.NotificationEnabled);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => page.ResetPreferenceGroupAsync(SettingsResetScope.All, CancellationToken.None));
        await fixture.Generations.DisposeAsync();
        Assert.Throws<DataGenerationManagerException>(() => page.ReadConnectionSamplingSettings());
    }
}

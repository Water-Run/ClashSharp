using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Integration;

public sealed partial class GenerationSettingsAuthorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeCommand_UnchangedChoiceObservesActualStateAndRepairsOnlyKnownDrift(bool drifted)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        SettingDesiredEntry before = fixture.Session.Snapshot.Desired[SettingsRegistry.Keys.NotificationEnabled];
        Participant participant = fixture.Participants[SettingApplicationKind.Internal];
        if (drifted) { participant.SetObserved(SettingsRegistry.Keys.NotificationEnabled, Change("NotificationEnabled", "false").Value); }

        SettingsAuthorityResult result = await fixture.Authority.ApplyRuntimeChangesAsync(
            [Change("NotificationEnabled", "true")], Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal(before, result.Envelope!.Desired[SettingsRegistry.Keys.NotificationEnabled]);
        Assert.Equal(drifted ? 1 : 0, participant.Applies);
        Assert.Equal(drifted ? 2 : 1, participant.Probes);
        Assert.Equal("true", result.Envelope.Applied[SettingsRegistry.Keys.NotificationEnabled].Value!.CanonicalText);
        Assert.Empty(result.Envelope.PendingApplications);
        Assert.Equal(Hash(result.Envelope), Hash((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!));
    }

    [Fact]
    public async Task RuntimeCommand_UnknownActualStateCannotReuseCachedSuccessOrApplyBlindly()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        Participant participant = fixture.Participants[SettingApplicationKind.Internal];
        participant.BeforeProbe = () => throw new IOException("observation unavailable");

        SettingsAuthorityResult result = await fixture.Authority.ApplyRuntimeChangesAsync(
            [Change("NotificationEnabled", "true")], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.Equal(0, participant.Applies);
        Assert.Equal(SettingAppliedStateKind.Unknown, result.Envelope!.Applied[SettingsRegistry.Keys.NotificationEnabled].Kind);
        Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single(result.Envelope.PendingApplications).State);
    }

    [Fact]
    public async Task RuntimeCommand_DoesNotSilentlyReplaceAFailedAttempt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Participant participant = fixture.Participants[SettingApplicationKind.Internal];
        participant.IgnoreEffects = true;
        SettingsAuthorityResult first = await fixture.Authority.ApplyRuntimeChangesAsync(
            [Change("NotificationEnabled", "false")], Guid.NewGuid(), CancellationToken.None);
        SettingsApplicationBatch failed = Assert.Single(first.Envelope!.PendingApplications);
        int applications = participant.Applies;
        int probes = participant.Probes;

        SettingsAuthorityResult repeated = await fixture.Authority.ApplyRuntimeChangesAsync(
            [Change("NotificationEnabled", "false")], Guid.NewGuid(), CancellationToken.None);

        Assert.False(repeated.IsSucceeded);
        SettingsApplicationBatch retained = Assert.Single(repeated.Envelope!.PendingApplications);
        Assert.Equal(failed.BatchId, retained.BatchId);
        Assert.Equal(failed.AttemptId, retained.AttemptId);
        Assert.Equal(SettingsApplicationBatchState.Failed, retained.State);
        Assert.Equal(applications, participant.Applies);
        Assert.Equal(probes, participant.Probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeCommand_ObservationPublicationOwnsItsEffectThroughDrainAndCancellation(bool callerOwnsLease)
    {
        using CancellationTokenSource cancellation = new();
        PublicationHook hook = new();
        await using Fixture fixture = await Fixture.CreateAsync(hook);
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        Participant participant = fixture.Participants[SettingApplicationKind.Internal];
        participant.SetObserved(SettingsRegistry.Keys.NotificationEnabled, Change("NotificationEnabled", "false").Value);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MutationAdmissionLease>? drain = null;
        MutationAdmissionLease? suppliedLease = callerOwnsLease ? fixture.Admission.AcquireOrdinary() : null;
        hook.AfterFirstPublication = () =>
        {
            cancellation.Cancel();
            drain = fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
        };
        participant.BeforeApply = async () => { entered.TrySetResult(); await release.Task; };
        Task<SettingsAuthorityResult> command = suppliedLease is null
            ? fixture.Authority.ApplyRuntimeChangesAsync([Change("NotificationEnabled", "true")], Guid.NewGuid(), cancellation.Token)
            : fixture.Authority.ApplyRuntimeChangesAdmittedAsync([Change("NotificationEnabled", "true")], Guid.NewGuid(), suppliedLease, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.NotNull(drain);
            Assert.False(drain.IsCompleted);
            Assert.False(command.IsCompleted);
        }
        finally { release.TrySetResult(); }
        try
        {
            SettingsAuthorityResult result = await command.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.IsSucceeded, result.Code);
            Assert.Empty(result.Envelope!.PendingApplications);
            if (suppliedLease is not null)
            {
                fixture.Admission.EnsureActiveLease(suppliedLease);
                Assert.False(drain!.IsCompleted);
            }
        }
        finally { suppliedLease?.Dispose(); }
        await using MutationAdmissionLease exclusive = await drain!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("true", fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.NotificationEnabled].Value!.CanonicalText);
    }
}

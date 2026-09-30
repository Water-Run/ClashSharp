extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;
using RecoveryPage = ClashSharpUi::ClashSharp.ViewModel.SettingsRecoveryViewModel;
using RecoveryViewModel = ClashSharpUi::ClashSharp.ViewModel.SettingsRecoveryViewModel;

namespace ClashSharp.Tests.Integration;

public sealed partial class GenerationSettingsAuthorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsRecovery_PageExplainsFailureOrStaleAttemptAndRefreshesTheDisplayedIdentity(bool stale)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var participant = fixture.Participants[SettingApplicationKind.Internal];
        participant.IgnoreEffects = true;
        _ = await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "false")], Guid.NewGuid(), CancellationToken.None);
        RecoveryViewModel page = new(key => key, fixture.Authority, new RecoveryFeedbackErrors(), () => { });
        page.Refresh();
        var issue = Assert.Single(page.Issues);
        var original = Assert.Single(issue.Snapshot.Envelope.PendingApplications);
        if (stale) { _ = await fixture.Authority.RetryAsync(original.BatchId, original.AttemptId, Guid.NewGuid(), CancellationToken.None); }
        int applications = participant.Applies;

        await page.RetryAsync(issue, CancellationToken.None);

        Assert.True(page.HasError);
        Assert.True(page.IsVisible);
        Assert.Equal(stale ? "Settings.Recovery.Stale" : "Settings.Recovery.RetryFailed", page.ErrorText);
        var refreshed = Assert.Single(page.Issues);
        Assert.NotEqual(original.AttemptId, Assert.Single(refreshed.Snapshot.Envelope.PendingApplications).AttemptId);
        if (stale) { Assert.Equal(applications, participant.Applies); }
    }

    [Fact]
    public async Task SettingsRecovery_SnapshotReadsNeverRetryAndExplicitRecoveryVerifiesTheSavedChoice()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var participant = fixture.Participants[SettingApplicationKind.Internal];
        participant.IgnoreEffects = true;
        var failed = await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "false")], Guid.NewGuid(), CancellationToken.None);
        int applications = participant.Applies;
        var snapshot = fixture.Authority.CaptureSnapshot();
        var batch = Assert.Single(failed.Envelope!.PendingApplications);
        _ = fixture.Authority.CaptureSnapshot();
        Assert.Equal(applications, participant.Applies);
        participant.IgnoreEffects = false;

        var result = await fixture.Authority.RetryApplicationAsync(snapshot, batch.BatchId, CancellationToken.None);

        Assert.True(result.Outcome.IsSucceeded);
        Assert.Empty(result.Outcome.Envelope!.PendingApplications);
        Assert.False(participant.GetObserved(SettingsRegistry.Keys.NotificationEnabled).Get<bool>());
        Assert.Equal(Hash(result.Outcome.Envelope), Hash((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsRecovery_StaleAttemptOrDifferentDataDirectoryCannotReplay(bool otherDirectory)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using Fixture other = await Fixture.CreateAsync();
        var participant = fixture.Participants[SettingApplicationKind.Internal];
        participant.IgnoreEffects = true;
        _ = await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "false")], Guid.NewGuid(), CancellationToken.None);
        var before = fixture.Authority.CaptureSnapshot();
        var batch = Assert.Single(before.Envelope.PendingApplications);
        if (otherDirectory) { before = new(other.Manifest.Descriptor, before.Envelope); }
        else { _ = await fixture.Authority.RetryAsync(batch.BatchId, batch.AttemptId, Guid.NewGuid(), CancellationToken.None); }
        int applications = participant.Applies;

        var result = await fixture.Authority.RetryApplicationAsync(before, batch.BatchId, CancellationToken.None);

        Assert.Equal(SettingsAuthorityStatus.Rejected, result.Outcome.Status);
        Assert.Equal("settings.recovery.stale_attempt", result.Outcome.Code);
        Assert.Equal(applications, participant.Applies);
    }

    [Fact]
    public async Task SettingsRecovery_CancellationAfterRetryPublicationCannotAbandonTheEffect()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var participant = fixture.Participants[SettingApplicationKind.Internal];
        participant.IgnoreEffects = true;
        _ = await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "false")], Guid.NewGuid(), CancellationToken.None);
        var before = fixture.Authority.CaptureSnapshot();
        var batch = Assert.Single(before.Envelope.PendingApplications);
        participant.IgnoreEffects = false;
        using CancellationTokenSource cancellation = new();
        participant.BeforeApply = () => { cancellation.Cancel(); return Task.CompletedTask; };

        var result = await fixture.Authority.RetryApplicationAsync(before, batch.BatchId, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Outcome.IsSucceeded);
        Assert.Empty(result.Outcome.Envelope!.PendingApplications);
    }

    [Fact]
    public async Task SettingsRecovery_NotificationFailureDoesNotHideACommittedRetry()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var participant = fixture.Participants[SettingApplicationKind.Internal];
        participant.IgnoreEffects = true;
        _ = await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "false")], Guid.NewGuid(), CancellationToken.None);
        var before = fixture.Authority.CaptureSnapshot();
        var batch = Assert.Single(before.Envelope.PendingApplications);
        participant.IgnoreEffects = false;
        IOException failure = new("subscriber unavailable");
        fixture.Authority.StateChanged += _ => throw failure;

        var result = await fixture.Authority.RetryApplicationAsync(before, batch.BatchId, CancellationToken.None);

        Assert.True(result.Outcome.IsSucceeded);
        Assert.Same(failure, result.NotificationFailure);
        Assert.Empty(result.Outcome.Envelope!.PendingApplications);
    }

    private sealed class RecoveryFeedbackErrors : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected recovery feedback error.", error.Exception);
    }
}

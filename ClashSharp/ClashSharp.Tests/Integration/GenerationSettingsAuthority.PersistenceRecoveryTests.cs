using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Integration;

public sealed partial class GenerationSettingsAuthorityTests
{
    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion)]
    public async Task InterruptedLayoutSave_PublishesTheRereadEnvelopeWithoutClaimingApplication(SettingsPersistenceFaultPoint cut)
    {
        PersistenceFailure fault = new(cut, occurrence: 1);
        await using Fixture fixture = await Fixture.CreateAsync(fault);
        Assert.True((await fixture.Authority.OpenAsync(CancellationToken.None)).IsSucceeded);
        string original = fixture.Authority.CaptureSnapshot().Envelope.Desired[SettingsRegistry.Keys.MasterInfoTileLayout].Value.CanonicalText;
        List<SettingsAuthoritySnapshot> publications = [];
        fixture.Authority.StateChanged += snapshot =>
        {
            Assert.Equal(Hash(snapshot.Envelope), Hash(fixture.Authority.CaptureSnapshot().Envelope));
            publications.Add(snapshot);
        };

        SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
            [Change("MasterInfoTileLayout", "core,port")], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, failed.Status);
        Assert.Equal(SettingsPersistenceStatus.Unavailable, failed.PersistenceStatus);
        Assert.Equal("settings.persistence.unavailable", failed.Code);
        Assert.False(failed.IsSucceeded);
        SettingsAuthoritySnapshot published = Assert.Single(publications);
        Assert.Equal(fixture.Manifest.Descriptor.GenerationId, published.Generation.GenerationId);
        Assert.Equal(Hash(failed.Envelope!), Hash(published.Envelope));
        Assert.Equal(Hash((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!), Hash(published.Envelope));
        Assert.Equal(cut == SettingsPersistenceFaultPoint.BeforeEnvelopePromotion ? original : "core,port",
            published.Envelope.Desired[SettingsRegistry.Keys.MasterInfoTileLayout].Value.CanonicalText);
        Assert.Equal(original, published.Envelope.Applied[SettingsRegistry.Keys.MasterInfoTileLayout].Value!.CanonicalText);
        Assert.All(fixture.Participants.Values, participant =>
        {
            Assert.Equal(0, participant.Probes);
            Assert.Equal(0, participant.Applies);
        });
        Assert.Equal(1, fault.Failures);
    }

    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion)]
    public async Task InterruptedCompletion_RereadsPendingOrVerifiedEvidenceWithoutRepeatingTheEffect(SettingsPersistenceFaultPoint cut)
    {
        PersistenceFailure fault = new(cut, occurrence: 3);
        await using Fixture fixture = await Fixture.CreateAsync(fault);

        SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
            [Change("MixedPort", "7890")], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, failed.Status);
        Assert.Equal("settings.persistence.unavailable", failed.Code);
        Assert.False(failed.IsSucceeded);
        SettingsEnvelope current = fixture.Authority.CaptureSnapshot().Envelope;
        Assert.Equal(Hash(current), Hash(failed.Envelope!));
        Assert.Equal(Hash(current), Hash((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!));
        if (cut == SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)
        {
            Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single(current.PendingApplications).State);
            Assert.Equal(SettingAppliedStateKind.Unknown, current.Applied[SettingsRegistry.Keys.MixedPort].Kind);
        }
        else
        {
            Assert.Empty(current.PendingApplications);
            Assert.Equal("7890", current.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
        }
        Assert.Equal(1, fixture.Participants[SettingApplicationKind.Network].Applies);
        Assert.Equal(2, fixture.Participants[SettingApplicationKind.Network].Probes);
        Assert.Equal(1, fault.Failures);
    }

    [Fact]
    public async Task InterruptedSave_WithStorageStillUnavailable_DoesNotPublishOldOrUnverifiedValues()
    {
        PersistenceFailure fault = new(SettingsPersistenceFaultPoint.AfterEnvelopePromotion, occurrence: 1);
        await using Fixture fixture = await Fixture.CreateAsync(fault);
        fault.OnFailure = () => fault.HoldFile(fixture.Repository.PrimaryPath);
        int publications = 0;
        fixture.Authority.StateChanged += _ => ++publications;
        try
        {
            SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
                [Change("MasterInfoTileLayout", "core,port")], Guid.NewGuid(), CancellationToken.None);

            Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, failed.Status);
            Assert.Equal(SettingsPersistenceStatus.Unavailable, failed.PersistenceStatus);
            Assert.Null(failed.Envelope);
            Assert.Equal(0, publications);
            Assert.Throws<InvalidOperationException>(() => fixture.Authority.CaptureSnapshot());
        }
        finally { fault.ReleaseFile(); }
        Assert.True((await fixture.Authority.OpenAsync(CancellationToken.None)).IsSucceeded);
        Assert.Equal("core,port", fixture.Authority.CaptureSnapshot().Envelope.Desired[SettingsRegistry.Keys.MasterInfoTileLayout].Value.CanonicalText);
    }

    [Fact]
    public async Task InterruptedCommittedSave_PageCancellationDoesNotSkipTheVerifiedReread()
    {
        using CancellationTokenSource caller = new();
        PersistenceFailure fault = new(SettingsPersistenceFaultPoint.AfterEnvelopePromotion, occurrence: 1)
        {
            OnFailure = caller.Cancel,
        };
        await using Fixture fixture = await Fixture.CreateAsync(fault);

        SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
            [Change("MasterInfoTileLayout", "core,port")], Guid.NewGuid(), caller.Token);

        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, failed.Status);
        Assert.Equal("core,port", fixture.Authority.CaptureSnapshot().Envelope.Desired[SettingsRegistry.Keys.MasterInfoTileLayout].Value.CanonicalText);
        Assert.Equal(0, fixture.Participants[SettingApplicationKind.Internal].Applies);
    }

    [Fact]
    public async Task InterruptedCommittedSave_AdmissionDrainWaitsForItsReadOnlyPublication()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        PersistenceFailure fault = new(SettingsPersistenceFaultPoint.AfterEnvelopePromotion, occurrence: 1);
        await using Fixture fixture = await Fixture.CreateAsync(fault);
        Task<MutationAdmissionLease>? drain = null;
        fault.OnFailure = () =>
        {
            drain = fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, deadline.Token).AsTask();
            Assert.False(drain.IsCompleted);
        };
        try
        {
            SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
                [Change("MasterInfoTileLayout", "core,port")], Guid.NewGuid(), deadline.Token);

            Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, failed.Status);
            Assert.Equal("core,port", fixture.Authority.CaptureSnapshot().Envelope.Desired[SettingsRegistry.Keys.MasterInfoTileLayout].Value.CanonicalText);
            Assert.NotNull(drain);
            await using MutationAdmissionLease drained = await drain;
            Assert.Equal(0, fixture.Participants[SettingApplicationKind.Internal].Applies);
        }
        finally
        {
            if (drain is not null) { await using MutationAdmissionLease drained = await drain; }
        }
    }

    private sealed class PersistenceFailure(SettingsPersistenceFaultPoint cut, int occurrence) : ISettingsPersistenceFaultInjector
    {
        private int _occurrences;
        private FileStream? _heldFile;
        public int Failures { get; private set; }
        public Action? OnFailure { get; set; }
        public void HoldFile(string path) => _heldFile = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        public void ReleaseFile() => Interlocked.Exchange(ref _heldFile, null)?.Dispose();
        public Task InjectAsync(SettingsPersistenceFaultPoint point, CancellationToken cancellationToken)
        {
            if (point == cut && ++_occurrences == occurrence)
            {
                ++Failures;
                OnFailure?.Invoke();
                throw new IOException("Controlled persistence interruption.");
            }
            return Task.CompletedTask;
        }
    }
}

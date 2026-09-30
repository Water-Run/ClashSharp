using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Exercises the real tile-layout services and admitted settings writer with an isolated value map.</summary>
public sealed class MasterLayoutSettingsBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayoutSave_PublishesOneCanonicalSettingAndHoldsAdmissionThroughNotification(bool hero)
    {
        AppSettingsService settings = new(new Dictionary<string, object>());
        MutationAdmissionBarrier barrier = new();
        settings.ConfigureMutationAdmission(barrier);
        ValueTask<MutationAdmissionLease> exclusive = default;
        List<AppSettingChangedEventArgs> observed = [];
        settings.SettingChanged += (_, change) =>
        {
            observed.Add(change);
            Assert.Equal(change.NewValue, hero ? settings.MasterHeroStatusLayout : settings.MasterInfoTileLayout);
            exclusive = barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            Assert.False(exclusive.IsCompleted);
        };

        await SaveAsync(settings, hero, CancellationToken.None);

        await using MutationAdmissionLease lease = await exclusive;
        AppSettingChangedEventArgs change = Assert.Single(observed);
        Assert.Equal(hero ? "MasterHeroStatusLayout" : "MasterInfoTileLayout", change.Key);
        Assert.False(change.WasRemoved);
        Assert.Equal(hero ? "Latency,CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic" : "latency,core", change.NewValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayoutSave_RefusesToWriteDuringAnExclusiveImportOrReset(bool hero)
    {
        Dictionary<string, object> values = [];
        AppSettingsService settings = new(values);
        MutationAdmissionBarrier barrier = new();
        settings.ConfigureMutationAdmission(barrier);
        await using MutationAdmissionLease exclusive = await barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive, CancellationToken.None);

        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(() => SaveAsync(settings, hero, CancellationToken.None));

        Assert.Empty(values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayoutSave_ObserverFailureRetainsThePersistedValueAndDoesNotReplay(bool hero)
    {
        Dictionary<string, object> values = [];
        AppSettingsService settings = new(values);
        IOException failure = new("Notification failed after storage publication");
        int notifications = 0;
        settings.SettingChanged += (_, _) => { notifications++; throw failure; };

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => SaveAsync(settings, hero, CancellationToken.None)));

        Assert.Single(values);
        Assert.Equal(1, notifications);
        Assert.Equal(
            hero ? "Latency,CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic" : "latency,core",
            hero ? settings.MasterHeroStatusLayout : settings.MasterInfoTileLayout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayoutSave_PreCancelledRequestDoesNotTouchStorage(bool hero)
    {
        Dictionary<string, object> values = [];
        AppSettingsService settings = new(values);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SaveAsync(settings, hero, new CancellationToken(true)));

        Assert.Empty(values);
    }

    private static async Task SaveAsync(AppSettingsService settings, bool hero, CancellationToken cancellationToken)
    {
        if (hero)
        {
            await new MasterHeroStatusLayoutService(settings).SaveLayoutAsync(
                [MasterHeroStatusItemKind.Latency, MasterHeroStatusItemKind.Latency], cancellationToken);
        }
        else
        {
            await new MasterInfoTileLayoutService(settings).SaveLayoutAsync(
                ["LATENCY", "core", "latency", "unknown"], ["core", "latency"], cancellationToken);
        }
    }
}

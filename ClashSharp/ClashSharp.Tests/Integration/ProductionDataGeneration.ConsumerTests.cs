extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model;
using ClashSharp.Settings;
using LatencyStorage = ClashSharpUi::ClashSharp.Service.ProxyLatencyStorageAdapter;
using LinkPage = ClashSharpUi::ClashSharp.Presentation.Adapters.SubscriptionLinkCatalogAdapter;
using LogFacade = ClashSharpUi::ClashSharp.Hosting.Data.GenerationLogStorage;
using LogFactory = ClashSharpUi::ClashSharp.Service.LogStorageServiceFactory;
using LogPage = ClashSharpUi::ClashSharp.Presentation.Adapters.LogManagementStoreAdapter;
using PageLog = ClashSharpUi::ClashSharp.Presentation.Adapters.PageLogAdapter;
using ProfileFacade = ClashSharpUi::ClashSharp.Hosting.Data.GenerationProfileCatalog;
using ProfilePage = ClashSharpUi::ClashSharp.Presentation.Adapters.ProfileManagementCatalogAdapter;
using SamplingStorage = ClashSharpUi::ClashSharp.Service.ConnectionSamplingStorageAdapter;
using StatisticsPage = ClashSharpUi::ClashSharp.Presentation.Adapters.StatisticsStoreAdapter;
using StatisticsProfiles = ClashSharpUi::ClashSharp.Presentation.Adapters.StatisticsProfilesAdapter;
using SubscriptionCatalog = ClashSharpUi::ClashSharp.Service.ProfileSubscriptionSchedulerCatalogAdapter;
using TrafficFacade = ClashSharpUi::ClashSharp.Hosting.Data.GenerationTriggerTrafficContextSource;
using TrafficSnapshot = ClashSharpUi::ClashSharp.Model.MihomoTrafficSnapshot;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task ExistingPagesAndBackgroundAdapters_FollowCommittedGenerationAndPreserveDetachedResults()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        LogFacade logs = new(fixture.Manager);
        ProfileFacade profiles = new(fixture.Manager);
        TrafficFacade traffic = new(fixture.Manager);
        LogPage logPage = new(logs);
        ProfilePage profilePage = new(profiles);
        LinkPage links = new(profiles);
        StatisticsPage statistics = new(logs);
        StatisticsProfiles names = new(profiles);
        PageLog pageLog = new(logs);
        SamplingStorage sampling = new(logs);
        LatencyStorage latency = new(logs);
        SubscriptionCatalog scheduler = new(profiles);
        _ = await links.AddSubscriptionLinkAsync("original", "https://example.test/original", CancellationToken.None);
        string input = await WriteProfileInputAsync(directory);
        var imported = await profilePage.ImportLocalProfileAsync(input, CancellationToken.None);
        sampling.AppendTrafficSnapshot(new TrafficSnapshot(Guid.NewGuid(), 100, 250, [Connection(10, 20)]));
        pageLog.Append("Info", "Pages", "original log", null);
        latency.UpsertNodeHealth("original node", "US", 12);
        var oldRows = logPage.GetLogs(20, null, null, null);
        var oldProfiles = names.GetProfileDisplayNamesById();
        Assert.Equal(100, statistics.GetTrafficStatisticsSummary().TotalUploadBytes);
        Assert.Equal(1, statistics.GetRuleHitCounts()["MATCH"]);
        Assert.Equal(350, (await traffic.ReadAsync([TimeSpan.FromHours(1)], true, DateTimeOffset.UtcNow, CancellationToken.None)).AllTimeTrafficBytes);

        await fixture.ReplaceWithEmptyGenerationAsync();

        Assert.NotEqual(baseline.Descriptor.GenerationId, fixture.Manager.CurrentManifest.Descriptor.GenerationId);
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[0].Logs.GetRecentLogs(5));
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[0].Profiles.GetProfiles());
        Assert.Empty(logPage.GetLogs(20, null, null, null));
        Assert.Empty(links.GetSubscriptionLinks());
        Assert.DoesNotContain(profilePage.GetProfiles(), row => row.Id == imported.ProfileId);
        Assert.DoesNotContain(imported.ProfileId, names.GetProfileDisplayNamesById().Keys);
        Assert.Equal(0, statistics.GetTrafficStatisticsSummary().TotalUploadBytes);
        Assert.Empty(statistics.GetRuleHitCounts());
        Assert.Null(logs.GetNodeLatencyMilliseconds("original node"));
        Assert.Empty(scheduler.GetDueSubscriptionLinks(DateTimeOffset.UtcNow));

        _ = await links.AddSubscriptionLinkAsync("replacement", "https://example.test/replacement", CancellationToken.None);
        sampling.AppendTrafficSnapshot(new TrafficSnapshot(Guid.NewGuid(), 800, 900, [Connection(80, 90)]));
        pageLog.Append("Warning", "Pages", "replacement log", null);
        latency.UpsertNodeHealth("replacement node", "JP", 34);
        Assert.Equal("replacement", Assert.Single(links.GetSubscriptionLinks()).Name);
        Assert.Equal("replacement log", Assert.Single(logPage.GetLogs(20, null, null, null)).Message);
        Assert.Equal(800, statistics.GetTrafficStatisticsSummary().TotalUploadBytes);
        Assert.Equal(34, logs.GetNodeLatencyMilliseconds("replacement node"));
        var trafficSnapshot = await traffic.ReadAsync([TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)], true, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1700, trafficSnapshot.AllTimeTrafficBytes);
        Assert.All(trafficSnapshot.RollingTrafficBytes.Values, value => Assert.Equal(1700, value));
        Assert.Equal("original log", Assert.Single(oldRows).Message);
        Assert.Contains(imported.ProfileId, oldProfiles.Keys);
        Assert.True(File.Exists(Path.Combine(baseline.Descriptor.RootPath, "ProfileCatalog.json")));

        await fixture.Manager.DisposeAsync();
        await using Fixture reopened = new(directory);
        _ = await reopened.StartAsync();
        Assert.Equal("replacement", Assert.Single(reopened.Containers[0].Profiles.GetSubscriptionLinks()).Name);
        Assert.Equal("replacement log", Assert.Single(reopened.Containers[0].Logs.GetRecentLogs(5)).Message);
        Assert.Equal(800, reopened.Containers[0].Logs.GetTrafficStatisticsSummary().TotalUploadBytes);
    }

    [Fact]
    public async Task AbortedGenerationReplacement_LeavesExistingPagesUsingTheOriginalRepositories()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        LogFacade logs = new(fixture.Manager);
        LinkPage links = new(new ProfileFacade(fixture.Manager));
        _ = await links.AddSubscriptionLinkAsync("retained", "https://example.test/retained", CancellationToken.None);
        logs.AppendLog("Info", "Pages", "retained", null);

        await using (MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None))
        await using (DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None))
        {
            transition.Stage(await fixture.CreateEmptyScopeAsync(admission));
            Assert.Throws<DataGenerationManagerException>(() => logs.GetRecentLogs(5));
            Assert.Throws<DataGenerationManagerException>(() => links.GetSubscriptionLinks());
            await transition.AbortAsync(directory.Store, CancellationToken.None);
        }

        Assert.Equal(baseline.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
        Assert.Equal("retained", Assert.Single(links.GetSubscriptionLinks()).Name);
        logs.AppendLog("Info", "Pages", "after abort", null);
        Assert.Equal(2, fixture.Containers[0].Logs.GetRecentLogs(5).Count);
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[1].Profiles.GetProfiles());
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[1].Logs.GetStorageSummary());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousStorageCommand_HoldsGenerationUntilEnumerationAndCommitFinish(bool failEnumeration)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        LogFacade logs = new(fixture.Manager);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        IEnumerable<ActiveConnection> EnumerateRows()
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException(); }
            if (failEnumeration) { throw new IOException("source enumeration failed"); }
            yield return Connection(10, 20);
        }
        Task<int> accepted = Task.Run(() => logs.AppendConnectionSnapshot(EnumerateRows()));
        Task<DataGenerationTransition>? drain = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            drain = fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None).AsTask();
            Assert.False(drain.IsCompleted);
            Assert.Throws<DataGenerationManagerException>(() => logs.AppendLog("Info", "Pages", "too late", null));
        }
        finally
        {
            release.Set();
        }
        if (failEnumeration) { await Assert.ThrowsAsync<IOException>(() => accepted); }
        else { Assert.Equal(1, await accepted); }
        await using DataGenerationTransition transition = await drain!.WaitAsync(TimeSpan.FromSeconds(5));
        await transition.AbortAsync();
        Assert.Equal(failEnumeration ? 0 : 1, logs.GetStorageSummary().ConnectionCount);
        logs.AppendLog("Info", "Pages", "after drain", null);
        Assert.Equal("after drain", Assert.Single(logs.GetRecentLogs(5)).Message);
    }

    [Fact]
    public async Task PageImport_KeepsGenerationAliveThroughValidationAndDurableCatalogPublication()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Validator.OnValidate = async _ => { entered.TrySetResult(); await release.Task; };
        ProfilePage page = new(new ProfileFacade(fixture.Manager));
        Task<ClashSharpUi::ClashSharp.Model.ProfileImportResult> import = page.ImportLocalProfileAsync(
            await WriteProfileInputAsync(directory), CancellationToken.None);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = fixture.Manager.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.All(fixture.Participants, participant => Assert.Equal(0, participant.Disposals));
            Assert.Throws<DataGenerationManagerException>(() => page.GetProfiles());
        }
        finally
        {
            release.TrySetResult();
        }
        var result = await import.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(result.ConfigPath));
        await using Fixture reopened = new(directory);
        _ = await reopened.StartAsync();
        Assert.Contains(reopened.Containers[0].Profiles.GetProfiles(), profile => profile.Id == result.ProfileId);
        Assert.Single(reopened.Containers[0].Profiles.GetProfileHistory(result.ProfileId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledImport_ReleasesItsGenerationAndDoesNotPublishPartialProfile(bool cancel)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        fixture.Validator.OnValidate = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new IOException("validation failed");
        };
        ProfilePage page = new(new ProfileFacade(fixture.Manager));
        var import = page.ImportLocalProfileAsync(await WriteProfileInputAsync(directory), cancellation.Token);
        Task<DataGenerationTransition>? drain = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            drain = fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None).AsTask();
            Assert.False(drain.IsCompleted);
            if (cancel) { cancellation.Cancel(); }
        }
        finally
        {
            release.TrySetResult();
        }
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import); }
        else { await Assert.ThrowsAsync<IOException>(() => import); }
        await using DataGenerationTransition transition = await drain!.WaitAsync(TimeSpan.FromSeconds(5));
        await transition.AbortAsync();
        Assert.Single(page.GetProfiles());
        Assert.False(await new ProfileFacade(fixture.Manager).TryUpdateSubscriptionLinkStatusAsync("absent", "unused", CancellationToken.None));
        Assert.Equal(baseline.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
    }

    [Fact]
    public async Task SubscriptionOutcomeCommand_RemainsPinnedWhileWaitingForMutationOwnership()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        ProfileFacade profiles = new(fixture.Manager);
        var link = await profiles.AddSubscriptionLinkAsync("scheduled", "https://example.test/scheduled", CancellationToken.None);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task gateOwner = fixture.MutationGate.ExecuteAsync(Guid.NewGuid(), async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DateTimeOffset attempted = DateTimeOffset.UtcNow;
        Task accepted = profiles.RecordSubscriptionUpdateOutcomeAsync(link.Id, true, attempted, CancellationToken.None);
        Task retirement = fixture.Manager.DisposeAsync().AsTask();
        try
        {
            Assert.Equal(1, fixture.MutationGate.QueuedCount);
            Assert.False(accepted.IsCompleted);
            Assert.False(retirement.IsCompleted);
            Assert.All(fixture.Participants, participant => Assert.Equal(0, participant.Disposals));
        }
        finally
        {
            release.TrySetResult();
        }
        await gateOwner.WaitAsync(TimeSpan.FromSeconds(5));
        await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await using Fixture reopened = new(directory);
        _ = await reopened.StartAsync();
        SubscriptionCatalog scheduler = new(new ProfileFacade(reopened.Manager));
        Assert.Empty(scheduler.GetDueSubscriptionLinks(attempted.AddMinutes(1)));
        Assert.Equal(link.Id, Assert.Single(scheduler.GetDueSubscriptionLinks(attempted.AddHours(link.UpdateIntervalHours + 1))).Id);
    }

    [Fact]
    public async Task UninitializedOrRetiredFacades_FailWithoutCreatingFallbackStorage()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        LogFacade logs = new(fixture.Manager);
        ProfileFacade profiles = new(fixture.Manager);
        TrafficFacade traffic = new(fixture.Manager);
        Assert.Throws<DataGenerationManagerException>(() => logs.GetStorageSummary());
        Assert.Throws<DataGenerationManagerException>(() => profiles.GetProfiles());
        await Assert.ThrowsAsync<DataGenerationManagerException>(() => profiles.AddSubscriptionLinkAsync("unused", "https://example.test/unused", CancellationToken.None));
        Assert.False(Directory.Exists(directory.RootPath));

        _ = await fixture.StartAsync();
        await fixture.Manager.DisposeAsync();
        Assert.Throws<DataGenerationManagerException>(() => logs.ClearAll());
        Assert.Throws<DataGenerationManagerException>(() => profiles.InvalidateCache());
        await Assert.ThrowsAsync<DataGenerationManagerException>(() => traffic.ReadAsync([], true, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[0].Logs.GetStorageSummary());
    }

    private static ActiveConnection Connection(long upload, long download) =>
        new("connection", "test.exe", "example.test", "MATCH", "", "DIRECT", upload, download, DateTimeOffset.UtcNow);

    private static async Task<string> WriteProfileInputAsync(DataGenerationTestDirectory directory)
    {
        string path = Path.Combine(directory.RootPath, "consumer-profile.yaml");
        await File.WriteAllTextAsync(path, "proxies: []\nproxy-groups: []\nrules:\n  - MATCH,DIRECT\n");
        return path;
    }

    private sealed partial class Fixture
    {
        public async Task<DataGenerationScope> CreateEmptyScopeAsync(MutationAdmissionLease admission)
        {
            DataGenerationDescriptor descriptor = _directory.CreateGeneration(Manager.CurrentManifest.Descriptor.GenerationNumber + 1);
            JsonSettingsRepository settings = new(descriptor, SettingsRegistry.Default);
            SettingsPersistenceResult initialized = await new SettingsAuthorityBootstrapper(settings, this, new SettingsMigrationPlanner(SettingsRegistry.Default))
                .OpenAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.True(initialized.IsSucceeded);
            await File.WriteAllTextAsync(Path.Combine(descriptor.RootPath, "ProfileCatalog.json"), "{\"Profiles\":[],\"Links\":[]}");
            await using (var logs = LogFactory.CreateForDirectory(descriptor.RootPath, () => "builtin-direct"))
            {
                _ = logs.GetStorageSummary();
            }
            SqliteTriggerRepository triggers = new(Path.Combine(descriptor.RootPath, "Triggers.db"));
            Assert.True((await triggers.OpenAsync(CancellationToken.None)).IsSucceeded);
            return await _factory.OpenAsync(descriptor, admission, CancellationToken.None);
        }

        public async Task ReplaceWithEmptyGenerationAsync()
        {
            await using MutationAdmissionLease admission = await Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            await using DataGenerationTransition transition = await Manager.BeginDrainAsync(Manager.CurrentManifest.ContentHash, CancellationToken.None);
            transition.Stage(await CreateEmptyScopeAsync(admission));
            _ = await transition.PromoteManifestAsync(_directory.Store, CancellationToken.None);
            transition.SwapToPromoted();
            await transition.CommitAsync();
        }
    }
}

extern alias ClashSharpUi;
using ClashSharp.Model;
using ClashSharp.Service;
using PublicationGate = ClashSharpUi::ClashSharp.Hosting.Data.GenerationPublicationGate;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for automatic profile subscription scheduling.</summary>
public sealed class ProfileSubscriptionSchedulerTests
{
    [Fact]
    public async Task ReplacementPublication_SubscriptionPassCannotReadOrImportBeforePublication()
    {
        PublicationGate gate = new();
        FakeSchedulerCatalog catalog = new([Link("held")]);
        ProfileSubscriptionScheduler scheduler = new(catalog, TimeProvider.System, (_, _, _, _) => { }, waitForExecution: gate.WaitAsync);
        Task pass = scheduler.UpdateDueSubscriptionsAsync(CancellationToken.None);
        Assert.False(pass.IsCompleted);
        Assert.Equal(0, catalog.Reads);
        Assert.Empty(catalog.ImportedLinkIds);

        gate.Publish();
        await pass.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, catalog.Reads);
        Assert.Equal(["held"], catalog.ImportedLinkIds);
    }

    [Fact]
    public async Task ReplacementPublication_QuiescenceCancelsAWaitingSubscriptionPassAndResumeRemainsHeld()
    {
        PublicationGate gate = new();
        FakeSchedulerCatalog catalog = new([Link("resumed")]);
        TaskCompletionSource firstWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource imported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int waits = 0;
        catalog.Imported = () => imported.TrySetResult();
        ProfileSubscriptionScheduler scheduler = new(catalog, TimeProvider.System, (_, _, _, _) => { }, waitForExecution: token =>
        {
            if (Interlocked.Increment(ref waits) == 1) { firstWait.TrySetResult(); }
            else { secondWait.TrySetResult(); }
            return gate.WaitAsync(token);
        });
        try
        {
            await scheduler.StartAsync(CancellationToken.None);
            await firstWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var paused = await scheduler.QuiesceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(paused.WasRunning);
            Assert.Equal(0, catalog.Reads);

            await scheduler.ResumeAsync(paused, CancellationToken.None);
            await secondWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(catalog.ImportedLinkIds);
            gate.Publish();
            await imported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["resumed"], catalog.ImportedLinkIds);
        }
        finally { await scheduler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UpdateDueSubscriptionsAsync_ContinuesAfterOneLinkFails()
    {
        DateTimeOffset now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        ProfileSubscriptionLink first = Link("first");
        ProfileSubscriptionLink second = Link("second");
        FakeSchedulerCatalog catalog = new([first, second]) { FailingLinkId = first.Id };
        List<(string Level, string Message, string? Detail)> logs = [];
        ProfileSubscriptionScheduler scheduler = new(
            catalog,
            new FixedTimeProvider(now),
            (level, _, message, detail) => logs.Add((level, message, detail)));

        await scheduler.UpdateDueSubscriptionsAsync(CancellationToken.None);

        Assert.Equal(["first", "second"], catalog.ImportedLinkIds);
        Assert.Contains(logs, log => log.Level == "Warning" && log.Detail == "first:HttpRequestException");
        Assert.Contains(logs, log => log.Level == "Info" && log.Detail == "subscription-second");
        Assert.Equal(now, catalog.ObservedNow);
    }

    private static ProfileSubscriptionLink Link(string id)
    {
        return new ProfileSubscriptionLink(
            id,
            id,
            $"https://example.com/{id}",
            true,
            24,
            DateTimeOffset.UnixEpoch,
            "ready");
    }

    private sealed class FakeSchedulerCatalog(IReadOnlyList<ProfileSubscriptionLink> dueLinks) :
        IProfileSubscriptionSchedulerCatalog
    {
        public string? FailingLinkId { get; init; }
        public int Reads { get; private set; }
        public Action? Imported { get; set; }

        public List<string> ImportedLinkIds { get; } = [];

        public DateTimeOffset ObservedNow { get; private set; }

        public IReadOnlyList<ProfileSubscriptionLink> GetDueSubscriptionLinks(DateTimeOffset now)
        {
            ++Reads;
            ObservedNow = now;
            return dueLinks;
        }

        public Task RetryPendingProfileCleanupAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<ProfileImportResult?> ImportDueSubscriptionLinkAsync(
            ProfileSubscriptionLink link,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ImportedLinkIds.Add(link.Id);
            Imported?.Invoke();
            if (StringComparer.Ordinal.Equals(link.Id, FailingLinkId))
            {
                throw new HttpRequestException("simulated failure");
            }

            return Task.FromResult<ProfileImportResult?>(new ProfileImportResult(
                "subscription-" + link.Id,
                link.Name,
                link.Id + ".yaml",
                1,
                1,
                "valid"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}

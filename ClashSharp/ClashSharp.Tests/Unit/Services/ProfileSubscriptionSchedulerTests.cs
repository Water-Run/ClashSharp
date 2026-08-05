using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for automatic profile subscription scheduling.</summary>
public sealed class ProfileSubscriptionSchedulerTests
{
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

        public List<string> ImportedLinkIds { get; } = [];

        public DateTimeOffset ObservedNow { get; private set; }

        public IReadOnlyList<ProfileSubscriptionLink> GetDueSubscriptionLinks(DateTimeOffset now)
        {
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

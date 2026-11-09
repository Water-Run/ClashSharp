using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>
/// Contracts of subscription download over a real loopback socket: whatever the server does, the catalog either
/// commits exactly the served text (atomically, with matching history) or commits nothing, records a failure for
/// the scheduler and releases every resource it staged.
/// </summary>
public sealed class SubscriptionTransportContractTests
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const string ValidProfile = "proxies: []\nproxy-groups: []\nrules:\n  - MATCH,DIRECT\n";

    private static readonly TimeSpan Overall = TimeSpan.FromSeconds(30);

    private sealed record Scenario(
        string Name,
        Func<HttpListenerContext, CancellationToken, Task> Handler,
        string? ExpectedText = null,
        TimeSpan? DownloadTimeout = null);

    // ---------------------------------------------------------------- universes

    private static IEnumerable<Scenario> FailureScenarios()
    {
        foreach (int status in new[] { 400, 401, 403, 404, 405, 408, 410, 418, 429, 451, 500, 501, 502, 503, 504 })
        {
            yield return new($"status-{status}", (c, _) => Reply.TextAsync(c, status, ValidProfile));
        }

        yield return new("status-304-empty", (c, _) =>
        {
            c.Response.StatusCode = 304;
            return Task.CompletedTask;
        });
        yield return new("redirect-loop", (c, _) => Reply.RedirectAsync(c, 302, "/loop"));
        yield return new("redirect-to-error", (c, _) => c.Request.Url!.AbsolutePath == "/start"
            ? Reply.RedirectAsync(c, 302, "/boom")
            : Reply.TextAsync(c, 500, "no"));
        yield return new("redirect-chain-exceeds-client-limit", (c, _) =>
        {
            string path = c.Request.Url!.AbsolutePath;
            int hop = path.StartsWith("/hop/", StringComparison.Ordinal)
                ? int.Parse(path[5..], CultureInfo.InvariantCulture)
                : 0;
            return Reply.RedirectAsync(c, 302, "/hop/" + (hop + 1).ToString(CultureInfo.InvariantCulture));
        });
        yield return new("content-length-over-limit", (c, _) =>
            Reply.BytesAsync(c, 200, Filler(MaxBytes + 1)));
        yield return new("chunked-over-limit", (c, _) =>
            Reply.BytesAsync(c, 200, Filler(MaxBytes + 1), chunked: true));
        yield return new("chunked-far-over-limit", (c, _) =>
            Reply.BytesAsync(c, 200, Filler(MaxBytes * 2), chunked: true));
        yield return new("truncated-body", (c, _) =>
            Reply.TruncatedAsync(c, 1000, Encoding.UTF8.GetBytes("proxies: []")));
        yield return new("truncated-before-first-body-byte", (c, _) =>
            Reply.TruncatedAsync(c, 1000, []));
        yield return new("connection-dropped-before-headers", (c, _) =>
        {
            c.Response.Abort();
            return Task.CompletedTask;
        });
        yield return new("invalid-utf8-latin1-byte", (c, _) =>
            Reply.BytesAsync(c, 200, [.. "name: caf"u8.ToArray(), 0xE9, (byte)'\n']));
        yield return new("invalid-utf8-overlong-slash", (c, _) =>
            Reply.BytesAsync(c, 200, [0xC0, 0xAF]));
        yield return new("invalid-utf8-encoded-surrogate", (c, _) =>
            Reply.BytesAsync(c, 200, [0xED, 0xA0, 0x80]));
        yield return new("invalid-utf8-truncated-sequence-at-end", (c, _) =>
            Reply.BytesAsync(c, 200, [(byte)'a', 0xE2, 0x82]));
        yield return new("gbk-encoded-text", (c, _) =>
            Reply.BytesAsync(c, 200, [0xC4, 0xE3, 0xBA, 0xC3]));
        yield return new("utf16-with-bom", (c, _) =>
            Reply.BytesAsync(c, 200, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(ValidProfile)],
                "text/plain; charset=utf-16"));
        yield return new("headers-never-arrive", (_, token) => Task.Delay(Timeout.Infinite, token),
            DownloadTimeout: TimeSpan.FromMilliseconds(300));
        yield return new("body-stalls-after-headers", async (c, token) =>
        {
            c.Response.ContentLength64 = 1000;
            await c.Response.OutputStream.WriteAsync("proxies"u8.ToArray(), token);
            await c.Response.OutputStream.FlushAsync(token);
            await Task.Delay(Timeout.Infinite, token);
        }, DownloadTimeout: TimeSpan.FromMilliseconds(300));
    }

    private static IEnumerable<Scenario> SuccessScenarios()
    {
        yield return new("plain", (c, _) => Reply.TextAsync(c, 200, ValidProfile), ValidProfile);
        yield return new("no-content-type", (c, _) => Reply.TextAsync(c, 200, ValidProfile, contentType: null), ValidProfile);
        yield return new("octet-stream", (c, _) => Reply.TextAsync(c, 200, ValidProfile, "application/octet-stream"), ValidProfile);
        yield return new("chunked", (c, _) => Reply.TextAsync(c, 200, ValidProfile, chunked: true), ValidProfile);
        yield return new("crlf-preserved", (c, _) => Reply.TextAsync(c, 200, "proxies: []\r\nrules:\r\n  - MATCH,DIRECT\r\n"),
            "proxies: []\r\nrules:\r\n  - MATCH,DIRECT\r\n");
        yield return new("utf8-bom-stripped", (c, _) =>
            Reply.BytesAsync(c, 200, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(ValidProfile)]), ValidProfile);
        yield return new("declared-charset-is-not-trusted-over-utf8-bytes",
            (c, _) => Reply.TextAsync(c, 200, ValidProfile, "text/plain; charset=iso-8859-1"), ValidProfile);
        yield return new("unicode-and-rtl", (c, _) => Reply.TextAsync(c, 200, "# 节点 ‮evil‬ עברית 😀\nrules: []\n"),
            "# 节点 ‮evil‬ עברית 😀\nrules: []\n");
        yield return new("empty-body", (c, _) => Reply.TextAsync(c, 200, string.Empty), string.Empty);
        yield return new("exactly-at-limit-content-length", (c, _) =>
            Reply.BytesAsync(c, 200, Filler(MaxBytes)), Encoding.ASCII.GetString(Filler(MaxBytes)));
        yield return new("exactly-at-limit-chunked", (c, _) =>
            Reply.BytesAsync(c, 200, Filler(MaxBytes), chunked: true), Encoding.ASCII.GetString(Filler(MaxBytes)));
        foreach (int status in new[] { 301, 302, 303, 307, 308 })
        {
            yield return new($"redirect-{status}", (c, _) => c.Request.Url!.AbsolutePath == "/start"
                ? Reply.RedirectAsync(c, status, "/final")
                : Reply.TextAsync(c, 200, ValidProfile), ValidProfile);
        }

        yield return new("redirect-chain-within-client-limit", (c, _) =>
        {
            string path = c.Request.Url!.AbsolutePath;
            int hop = path.StartsWith("/hop/", StringComparison.Ordinal)
                ? int.Parse(path[5..], CultureInfo.InvariantCulture)
                : 0;
            return hop < 10
                ? Reply.RedirectAsync(c, 302, "/hop/" + (hop + 1).ToString(CultureInfo.InvariantCulture))
                : Reply.TextAsync(c, 200, ValidProfile);
        }, ValidProfile);
    }

    private static byte[] Filler(int length)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, (byte)'x');
        if (length > 0)
        {
            bytes[^1] = (byte)'\n';
        }

        return bytes;
    }

    public static TheoryData<string> FailureNames() => [.. FailureScenarios().Select(static scenario => scenario.Name)];

    public static TheoryData<string> SuccessNames() => [.. SuccessScenarios().Select(static scenario => scenario.Name)];

    // ---------------------------------------------------------------- failure contract

    [Theory]
    [MemberData(nameof(FailureNames))]
    public async Task Failure_CommitsNothingRecordsFailureAndBacksOff(string name)
    {
        Scenario scenario = FailureScenarios().Single(candidate => candidate.Name == name);
        await using CatalogHarness harness = new(scenario.DownloadTimeout);
        await using LoopbackHttpServer server = new(scenario.Handler);
        using CancellationTokenSource limit = new(Overall);
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync(
            "Provider", server.Url("/start"), limit.Token);
        string profileId = "subscription-" + link.Id;

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => harness.Service.ImportSubscriptionLinkAsync(link, limit.Token));

        Assert.True(
            failure is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException
                or InvalidDataException or ArgumentException,
            $"Unexpected failure type {failure.GetType().FullName}");
        AssertNothingCommitted(harness, link, profileId);

        // The failure is observable to the scheduler: the link backs off instead of being retried immediately.
        ProfileSubscriptionLink after = harness.Service.GetSubscriptionLinks().Single();
        Assert.Equal("ProfileCatalog.Subscription.UpdateFailed", after.Status);
        Assert.Equal(link.Revision, after.Revision);
        Assert.Equal(DateTimeOffset.MinValue, after.LastUpdatedAt);
        DateTimeOffset now = DateTimeOffset.Now;
        Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(now.AddMinutes(1)), due => due.Id == link.Id);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(now.AddMinutes(5).AddSeconds(30)), due => due.Id == link.Id);

        // A later healthy answer from the same link recovers completely.
        server.Handler = static (c, _) => Reply.TextAsync(c, 200, ValidProfile);
        ProfileSubscriptionLink current = harness.Service.GetSubscriptionLinks().Single();
        ProfileImportResult recovered = await harness.Service.ImportSubscriptionLinkAsync(current, limit.Token);
        Assert.Equal(profileId, recovered.ProfileId);
        Assert.Equal(ValidProfile, harness.Sources.Get(profileId));
        Assert.Equal("ProfileCatalog.Subscription.Updated", harness.Service.GetSubscriptionLinks().Single().Status);
    }

    private static void AssertNothingCommitted(CatalogHarness harness, ProfileSubscriptionLink link, string profileId)
    {
        Assert.Equal(0, harness.Core.ImportCalls);
        Assert.Null(harness.Sources.Get(profileId));
        Assert.Empty(harness.Service.GetProfileHistory(profileId));
        Assert.DoesNotContain(harness.Service.GetProfiles(), profile => profile.Id == profileId);
        Assert.Empty(harness.HistoryFiles());
        Assert.Empty(harness.PendingHistoryFiles());
        Assert.Equal(link.Uri, harness.Service.GetSubscriptionLinks().Single().Uri);
    }

    // ---------------------------------------------------------------- success contract

    [Theory]
    [MemberData(nameof(SuccessNames))]
    public async Task Success_CommitsExactlyTheServedTextWithMatchingHistory(string name)
    {
        Scenario scenario = SuccessScenarios().Single(candidate => candidate.Name == name);
        await using CatalogHarness harness = new();
        await using LoopbackHttpServer server = new(scenario.Handler);
        using CancellationTokenSource limit = new(Overall);
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync(
            "Provider", server.Url("/start"), limit.Token);

        ProfileImportResult result = await harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);

        string profileId = "subscription-" + link.Id;
        Assert.Equal(profileId, result.ProfileId);
        Assert.Equal(scenario.ExpectedText, harness.Sources.Get(profileId));
        ProfileHistoryEntry entry = Assert.Single(harness.Service.GetProfileHistory(profileId));
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scenario.ExpectedText!)));
        Assert.Equal(expectedHash, entry.ContentSha256);
        string historyFile = Assert.Single(harness.HistoryFiles());
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(historyFile))));
        Assert.Empty(harness.PendingHistoryFiles());

        ProfileSubscriptionLink updated = harness.Service.GetSubscriptionLinks().Single();
        Assert.Equal("ProfileCatalog.Subscription.Updated", updated.Status);
        Assert.True(updated.LastUpdatedAt > DateTimeOffset.UnixEpoch);
        // Success pushes the next automatic attempt a whole interval away.
        DateTimeOffset now = DateTimeOffset.Now;
        Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(now.AddHours(23)), due => due.Id == link.Id);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(now.AddHours(25)), due => due.Id == link.Id);
    }

    [Fact]
    public async Task Compressed_ResponseIsNeverCommittedAsGarbage()
    {
        byte[] compressed;
        using (MemoryStream buffer = new())
        {
            using (GZipStream gzip = new(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(ValidProfile));
            }

            compressed = buffer.ToArray();
        }

        await using CatalogHarness harness = new();
        await using LoopbackHttpServer server = new((c, _) => Reply.BytesAsync(
            c, 200, compressed, "text/plain", [("Content-Encoding", "gzip")]));
        using CancellationTokenSource limit = new(Overall);
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("Gz", server.Url(), limit.Token);
        string profileId = "subscription-" + link.Id;

        try
        {
            await harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or InvalidOperationException
            or HttpRequestException or ArgumentException)
        {
            AssertNothingCommitted(harness, link, profileId);
            return;
        }

        // Alternatively the client may decompress; then exactly the original text must be committed.
        Assert.Equal(ValidProfile, harness.Sources.Get(profileId));
    }

    // ---------------------------------------------------------------- request shape

    [Fact]
    public async Task ImportRequest_UsesGetForTheExactStoredUrlAndSendsNoCredentialsOrCookies()
    {
        await using CatalogHarness harness = new();
        await using LoopbackHttpServer server = new((c, _) => Reply.TextAsync(c, 200, ValidProfile));
        using CancellationTokenSource limit = new(Overall);
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync(
            "P", server.Url("/sub/a%20b?token=AbC&x=%7e"), limit.Token);

        await harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.StartsWith("/sub/a", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("token=AbC", request.PathAndQuery, StringComparison.Ordinal);
        Assert.False(request.Headers.ContainsKey("Cookie"));
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Check_FallsBackToGetWhenHeadIsUnsupportedAndNeverImports()
    {
        foreach (int unsupported in new[] { 405, 501 })
        {
            await using CatalogHarness harness = new();
            await using LoopbackHttpServer server = new((c, _) => c.Request.HttpMethod == "HEAD"
                ? Reply.TextAsync(c, unsupported, string.Empty)
                : Reply.TextAsync(c, 200, ValidProfile, headers: [("Subscription-Userinfo", "upload=1; download=2; total=3; expire=0")]));
            using CancellationTokenSource limit = new(Overall);
            ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", server.Url(), limit.Token);

            await harness.Service.CheckSubscriptionLinkAsync(link, limit.Token);

            Assert.Equal(["HEAD", "GET"], server.Requests.Select(request => request.Method).ToArray());
            Assert.Equal(0, harness.Core.ImportCalls);
            ProfileSubscriptionLink updated = harness.Service.GetSubscriptionLinks().Single();
            Assert.Equal(new SubscriptionUsage(1, 2, 3, 0), updated.Usage);
            Assert.Equal(DateTimeOffset.MinValue, updated.LastUpdatedAt);
        }
    }

    [Fact]
    public async Task Check_ErrorStatusRecordsCheckFailureWithoutTouchingTheSchedule()
    {
        await using CatalogHarness harness = new();
        await using LoopbackHttpServer server = new((c, _) => Reply.TextAsync(c, 503, "down"));
        using CancellationTokenSource limit = new(Overall);
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", server.Url(), limit.Token);
        DateTimeOffset now = DateTimeOffset.Now;
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(now), due => due.Id == link.Id);

        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Service.CheckSubscriptionLinkAsync(link, limit.Token));

        Assert.Equal("ProfileCatalog.Subscription.CheckFailed", harness.Service.GetSubscriptionLinks().Single().Status);
        // A manual check is not an update attempt: the link is still due and no backoff was recorded.
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(now.AddSeconds(1)), due => due.Id == link.Id);
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task SameLink_ConcurrentImportsAreSerializedThroughDownloadAndCommit()
    {
        await using CatalogHarness harness = new();
        TaskCompletionSource firstArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrent = 0;
        int maxConcurrent = 0;
        int arrivals = 0;
        await using LoopbackHttpServer server = new(async (c, _) =>
        {
            int number = Interlocked.Increment(ref arrivals);
            int now = Interlocked.Increment(ref concurrent);
            int seen;
            while ((seen = Volatile.Read(ref maxConcurrent)) < now
                && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen)
            {
            }

            if (number == 1)
            {
                firstArrived.SetResult();
                await releaseFirst.Task;
            }

            await Reply.TextAsync(c, 200, ValidProfile);
            Interlocked.Decrement(ref concurrent);
        });
        using CancellationTokenSource limit = new(Overall);
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", server.Url(), limit.Token);

        Task<ProfileImportResult> first = harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);
        await firstArrived.Task.WaitAsync(limit.Token);
        Task<ProfileImportResult> second = harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromMilliseconds(250)));
        Assert.Single(server.Requests);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(limit.Token);

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(2, harness.Service.GetProfileHistory("subscription-" + link.Id).Count);
    }

    [Fact]
    public async Task DifferentLinks_DownloadConcurrentlyWithoutSharingAGate()
    {
        await using CatalogHarness harness = new();
        TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        await using LoopbackHttpServer server = new(async (c, _) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                bothArrived.SetResult();
            }

            await bothArrived.Task;
            await Reply.TextAsync(c, 200, ValidProfile);
        });
        using CancellationTokenSource limit = new(Overall);
        ProfileSubscriptionLink one = await harness.Service.AddSubscriptionLinkAsync("One", server.Url("/one"), limit.Token);
        ProfileSubscriptionLink two = await harness.Service.AddSubscriptionLinkAsync("Two", server.Url("/two"), limit.Token);

        await Task.WhenAll(
            harness.Service.ImportSubscriptionLinkAsync(one, limit.Token),
            harness.Service.ImportSubscriptionLinkAsync(two, limit.Token)).WaitAsync(limit.Token);

        Assert.Equal(2, harness.Service.GetProfiles().Count(profile => profile.Id.StartsWith("subscription-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CallerCancellationDuringDownload_PreservesPreviousCommitAndIsNotAFailure()
    {
        await using CatalogHarness harness = new();
        string served = ValidProfile;
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        await using LoopbackHttpServer server = new(async (c, token) =>
        {
            if (Volatile.Read(ref hold))
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            await Reply.TextAsync(c, 200, served);
        });
        using CancellationTokenSource limit = new(Overall);
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", server.Url(), limit.Token);
        await harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);
        ProfileSubscriptionLink committed = harness.Service.GetSubscriptionLinks().Single();
        string profileId = "subscription-" + link.Id;
        DateTimeOffset now = DateTimeOffset.Now;
        bool dueBefore = harness.Service.GetDueSubscriptionLinks(now.AddHours(1)).Any();

        Volatile.Write(ref hold, true);
        using CancellationTokenSource caller = new();
        Task<ProfileImportResult> import = harness.Service.ImportSubscriptionLinkAsync(committed, caller.Token);
        await arrived.Task.WaitAsync(limit.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);

        Assert.Equal(served, harness.Sources.Get(profileId));
        Assert.Single(harness.Service.GetProfileHistory(profileId));
        Assert.Empty(harness.PendingHistoryFiles());
        Assert.Equal("ProfileCatalog.Status.Canceled", harness.Service.GetSubscriptionLinks().Single().Status);
        Assert.Equal(dueBefore, harness.Service.GetDueSubscriptionLinks(now.AddHours(1)).Any());
    }
}

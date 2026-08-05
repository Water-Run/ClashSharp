using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests the bounded, read-only GitHub release checker.</summary>
public sealed class GitHubReleaseUpdateCheckerTests
{
    [Fact]
    public async Task CheckAsync_NewerStableTag_UsesOnlyFixedEndpointAndReportsUpdate()
    {
        Uri? requestUri = null;
        ProductInfoHeaderValue? userAgent = null;
        using StubHandler handler = new(request =>
        {
            requestUri = request.RequestUri;
            userAgent = request.Headers.UserAgent.Single();
            return JsonResponse("""
                {
                  "tag_name": "v1.2.3",
                  "draft": false,
                  "prerelease": false,
                  "html_url": "https://attacker.invalid/installer.exe",
                  "assets": [{ "browser_download_url": "https://attacker.invalid/payload.exe" }]
                }
                """);
        });
        using HttpClient client = new(handler);
        GitHubReleaseUpdateChecker checker = new(client, "1.2.2.0");

        ApplicationUpdateCheckResult result = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateAvailability.UpdateAvailable, result.Availability);
        Assert.Equal("1.2.3", result.LatestVersion);
        Assert.Equal(GitHubReleaseUpdateChecker.LatestReleaseApiUri, requestUri);
        Assert.Equal("ClashSharp/1.2.2", userAgent?.ToString());
        Assert.Equal("https://github.com/Water-Run/ClashSharp/releases/latest", GitHubReleaseUpdateChecker.LatestReleasePageUri.ToString());
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0", "Current")]
    [InlineData("1.2.2", "1.2.3.0", "Current")]
    [InlineData("v2.0.0+build.4", "1.9.9.0", "UpdateAvailable")]
    [InlineData("v2.0.0-beta.1", "1.9.9.0", "Unavailable")]
    [InlineData("latest", "1.9.9.0", "Unavailable")]
    public async Task CheckAsync_StableVersionComparison_IsDeterministic(
        string tag,
        string currentVersion,
        string expected)
    {
        using StubHandler handler = new(_ => JsonResponse($$"""
            { "tag_name": "{{tag}}", "draft": false, "prerelease": false }
            """));
        using HttpClient client = new(handler);
        GitHubReleaseUpdateChecker checker = new(client, currentVersion);

        ApplicationUpdateCheckResult result = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(expected, result.Availability.ToString());
    }

    [Fact]
    public async Task CheckAsync_SecondRequest_UsesETagAndReusesCachedResultOnNotModified()
    {
        int callCount = 0;
        using StubHandler handler = new(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                Assert.Empty(request.Headers.IfNoneMatch);
                HttpResponseMessage first = JsonResponse(
                    """{ "tag_name": "v2.0.0", "draft": false, "prerelease": false }""");
                first.Headers.ETag = new EntityTagHeaderValue("\"release-2\"");
                return first;
            }

            Assert.Equal("\"release-2\"", request.Headers.IfNoneMatch.Single().Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        using HttpClient client = new(handler);
        GitHubReleaseUpdateChecker checker = new(client, "1.0.0.0");

        ApplicationUpdateCheckResult first = await checker.CheckAsync(CancellationToken.None);
        ApplicationUpdateCheckResult second = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(ApplicationUpdateAvailability.UpdateAvailable, second.Availability);
        Assert.Equal(2, callCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckAsync_GitHubFailure_DegradesToUnavailable(HttpStatusCode statusCode)
    {
        using StubHandler handler = new(_ => new HttpResponseMessage(statusCode));
        using HttpClient client = new(handler);
        GitHubReleaseUpdateChecker checker = new(client, "1.0.0.0");

        ApplicationUpdateCheckResult result = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateAvailability.Unavailable, result.Availability);
    }

    [Fact]
    public async Task CheckAsync_OversizedPayload_IsRejectedBeforeParsing()
    {
        using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(64 * 1024) + 1]),
        });
        using HttpClient client = new(handler);
        GitHubReleaseUpdateChecker checker = new(client, "1.0.0.0");

        ApplicationUpdateCheckResult result = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateAvailability.Unavailable, result.Availability);
    }

    [Fact]
    public async Task CheckAsync_CachedSuccessSurvivesLaterRateLimit()
    {
        int callCount = 0;
        using StubHandler handler = new(_ => ++callCount == 1
            ? JsonResponse("""{ "tag_name": "v2.0.0" }""")
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using HttpClient client = new(handler);
        GitHubReleaseUpdateChecker checker = new(client, "1.0.0.0");

        ApplicationUpdateCheckResult first = await checker.CheckAsync(CancellationToken.None);
        ApplicationUpdateCheckResult second = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(ApplicationUpdateAvailability.UpdateAvailable, second.Availability);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}

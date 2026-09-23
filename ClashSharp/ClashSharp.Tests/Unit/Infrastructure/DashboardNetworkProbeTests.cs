using System.Net;
using System.Text.Json;
using ClashSharp.Infrastructure.Networking;

namespace ClashSharp.Tests.Unit.Infrastructure;

public sealed class DashboardNetworkProbeTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task Website_RetainsHttpStatusWithoutReadingBody(int status)
    {
        Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        DashboardNetworkProbe probe = new(() => new HttpClient(handler, false));
        WebsiteProbeResult result = await probe.CheckWebsiteAsync("https://example.invalid/check", CancellationToken.None);
        Assert.Equal(status, result.StatusCode);
        Assert.NotNull(result.LatencyMilliseconds);
        Assert.Null(result.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("bad input")]
    public async Task Website_InvalidUrlDoesNotCreateNetworkClient(string url)
    {
        DashboardNetworkProbe probe = new(() => throw new InvalidOperationException("No client expected"));
        Assert.Equal("invalid-url", (await probe.CheckWebsiteAsync(url, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationRemainDistinct()
    {
        Handler handler = new(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); });
        DashboardNetworkProbe probe = new(() => new HttpClient(handler, false), TimeSpan.FromMilliseconds(25));
        Assert.Equal("timeout", (await probe.CheckWebsiteAsync("https://example.invalid", CancellationToken.None)).Failure);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckWebsiteAsync("https://example.invalid", cancellation.Token));
    }

    [Fact]
    public async Task PublicIp_ParsesBoundedMetadataWithoutAutomaticallySendingRequests()
    {
        Handler handler = Json("""
            {"success":true,"ip":"2001:db8::1","country":"Test","region":"Test","city":"City",
             "connection":{"asn":64500,"isp":"ISP\nname","org":"Organization"},"timezone":{"id":"Etc/UTC"}}
            """);
        DashboardNetworkProbe probe = new(() => new HttpClient(handler, false));
        Assert.Equal(0, handler.Calls);
        PublicIpInformation result = await probe.GetPublicIpAsync(CancellationToken.None);
        Assert.Equal("2001:db8::1", result.Address);
        Assert.Equal("Test · City", result.Location);
        Assert.Equal("AS64500", result.Asn);
        Assert.Equal("ISPname", result.Isp);
        Assert.Equal("Organization", result.Organization);
        Assert.Equal("Etc/UTC", result.Timezone);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"success\":false,\"ip\":\"203.0.113.1\"}")]
    [InlineData("{\"success\":true,\"ip\":\"not-an-address\"}")]
    public async Task PublicIp_RejectsInvalidSuccessPayload(string json)
    {
        DashboardNetworkProbe probe = new(() => new HttpClient(Json(json)));
        await Assert.ThrowsAsync<JsonException>(() => probe.GetPublicIpAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PublicIp_MissingOptionalMetadataRemainsEmpty()
    {
        DashboardNetworkProbe probe = new(() => new HttpClient(Json("""{"success":true,"ip":"203.0.113.1","connection":{"asn":"bad"}}""")));
        PublicIpInformation result = await probe.GetPublicIpAsync(CancellationToken.None);
        Assert.Empty(result.Asn);
        Assert.Empty(result.Location);
        Assert.Empty(result.Timezone);
    }

    [Fact]
    public async Task PublicIp_RejectsOversizedBody()
    {
        DashboardNetworkProbe probe = new(() => new HttpClient(Json(new string('x', 32769))));
        await Assert.ThrowsAsync<HttpRequestException>(() => probe.GetPublicIpAsync(CancellationToken.None));
    }

    private static Handler Json(string text) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) }));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}

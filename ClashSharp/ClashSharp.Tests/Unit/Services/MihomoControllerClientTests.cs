/*
 * Mihomo Controller Client Tests
 * Verifies mihomo external-controller request shapes and JSON parsing
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/MihomoControllerClientTests.cs
 * @date: 2026-06-24
 */

using System.Net;
using System.Net.Http;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for mihomo external-controller client behavior.</summary>
public sealed class MihomoControllerClientTests
{
    /// <summary>Verifies close-all sends DELETE /connections.</summary>
    [Fact]
    public async Task CloseAllConnectionsAsync_SendsDeleteConnections()
    {
        RecordingHttpHandler handler = new("""{"connections":[]}""");
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        await client.CloseAllConnectionsAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/connections", handler.Requests[0].RequestUri?.AbsolutePath);
    }

    /// <summary>Verifies closing one connection sends DELETE /connections/{id} with URI escaping.</summary>
    [Fact]
    public async Task CloseConnectionAsync_SendsEscapedDeleteConnection()
    {
        RecordingHttpHandler handler = new("""{}""");
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        await client.CloseConnectionAsync("conn 1", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/connections/conn%201", handler.Requests[0].RequestUri?.AbsolutePath);
    }

    /// <summary>Verifies strategy groups are parsed from /proxies and leaf proxy rows are ignored.</summary>
    [Fact]
    public async Task GetProxyGroupsAsync_ParsesSelectableGroups()
    {
        RecordingHttpHandler handler = new("""
            {
              "proxies": {
                "Proxy": { "name": "Proxy", "type": "Selector", "now": "Node A", "all": ["Node A", "DIRECT"] },
                "Node A": { "name": "Node A", "type": "Shadowsocks" }
              }
            }
            """);
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        IReadOnlyList<MihomoProxyGroup> groups = await client.GetProxyGroupsAsync(CancellationToken.None);

        MihomoProxyGroup group = Assert.Single(groups);
        Assert.Equal("Proxy", group.Name);
        Assert.Equal("Selector", group.Type);
        Assert.Equal("Node A", group.CurrentSelection);
        Assert.Equal(["Node A", "DIRECT"], group.Candidates);
    }

    /// <summary>Verifies selecting a strategy group proxy sends PUT /proxies/{group} with the selected name.</summary>
    [Fact]
    public async Task SelectProxyAsync_SendsPutProxySelection()
    {
        RecordingHttpHandler handler = new("""{}""");
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        await client.SelectProxyAsync("Proxy Group", "Node A", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/proxies/Proxy%20Group", handler.Requests[0].RequestUri?.AbsolutePath);
        Assert.Equal("""{"name":"Node A"}""", handler.Bodies[0]);
    }

    /// <summary>Verifies proxy provider resources are parsed from /providers/proxies.</summary>
    [Fact]
    public async Task GetProxyProvidersAsync_ParsesProviderResources()
    {
        RecordingHttpHandler handler = new("""
            {
              "providers": {
                "sub": {
                  "name": "sub",
                  "type": "Proxy",
                  "vehicleType": "HTTP",
                  "updatedAt": "2026-06-24T01:02:03Z",
                  "proxies": [{ "name": "Node A" }, { "name": "Node B" }]
                }
              }
            }
            """);
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        IReadOnlyList<MihomoProviderResource> providers = await client.GetProxyProvidersAsync(CancellationToken.None);

        MihomoProviderResource provider = Assert.Single(providers);
        Assert.Equal("sub", provider.Name);
        Assert.Equal(MihomoProviderKind.Proxy, provider.Kind);
        Assert.Equal("HTTP", provider.VehicleType);
        Assert.Equal(2, provider.ItemCount);
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 1, 2, 3, TimeSpan.Zero), provider.UpdatedAt);
    }

    /// <summary>Verifies rule provider resources are parsed from /providers/rules.</summary>
    [Fact]
    public async Task GetRuleProvidersAsync_ParsesProviderResources()
    {
        RecordingHttpHandler handler = new("""
            {
              "providers": {
                "reject": {
                  "name": "reject",
                  "type": "Rule",
                  "behavior": "domain",
                  "ruleCount": 123,
                  "updatedAt": "2026-06-24T01:02:03Z"
                }
              }
            }
            """);
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        IReadOnlyList<MihomoProviderResource> providers = await client.GetRuleProvidersAsync(CancellationToken.None);

        MihomoProviderResource provider = Assert.Single(providers);
        Assert.Equal("reject", provider.Name);
        Assert.Equal(MihomoProviderKind.Rule, provider.Kind);
        Assert.Equal("domain", provider.Behavior);
        Assert.Equal(123, provider.ItemCount);
    }

    /// <summary>Verifies provider updates target the correct provider namespace.</summary>
    [Theory]
    [InlineData(MihomoProviderKind.Proxy, "/providers/proxies/sub")]
    [InlineData(MihomoProviderKind.Rule, "/providers/rules/reject")]
    public async Task UpdateProviderAsync_SendsPutToProviderEndpoint(MihomoProviderKind kind, string expectedPath)
    {
        string providerName = kind == MihomoProviderKind.Proxy ? "sub" : "reject";
        RecordingHttpHandler handler = new("""{}""");
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        await client.UpdateProviderAsync(new MihomoProviderResource(providerName, kind, "", "", 0, null), CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal(expectedPath, handler.Requests[0].RequestUri?.AbsolutePath);
    }

    /// <summary>HTTP handler that records requests and returns a configured JSON response.</summary>
    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public RecordingHttpHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}

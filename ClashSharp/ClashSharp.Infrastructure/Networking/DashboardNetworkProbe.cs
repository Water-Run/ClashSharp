using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace ClashSharp.Infrastructure.Networking;

/// <summary>A bounded website check through the current Windows network route.</summary>
public sealed record WebsiteProbeResult(string Url, int? StatusCode, long? LatencyMilliseconds, string? Failure);

/// <summary>Public metadata returned for the route used to reach the IP information provider.</summary>
public sealed record PublicIpInformation(string Address, string Location, string Asn, string Isp, string Organization, string Timezone);

/// <summary>Runs explicit dashboard checks; construction and reading stored results never send traffic.</summary>
public sealed class DashboardNetworkProbe
{
    private readonly Func<HttpClient> _createClient;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a probe with a fresh connection pool for each user-requested check.</summary>
    public DashboardNetworkProbe(Func<HttpClient>? createClient = null, TimeSpan? timeout = null)
    {
        _createClient = createClient ?? (() => new HttpClient());
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        if (_timeout <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
    }

    /// <summary>Checks an HTTP(S) destination without downloading the response body.</summary>
    public async Task<WebsiteProbeResult> CheckWebsiteAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return new(url, null, null, "invalid-url");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using HttpClient client = _createClient();
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return new(url, (int)response.StatusCode, watch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(url, null, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new(url, null, null, "network");
        }
    }

    /// <summary>Looks up the current public egress at ipwho.is only when explicitly requested.</summary>
    public async Task<PublicIpInformation> GetPublicIpAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using HttpClient client = _createClient();
        using HttpResponseMessage response = await client.GetAsync(
            "https://ipwho.is/?fields=success,ip,country,region,city,connection,timezone.id",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(32768, timeout.Token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out JsonElement success) || success.ValueKind != JsonValueKind.True
            || !IPAddress.TryParse(Read(root, "ip"), out IPAddress? address))
        {
            throw new JsonException("The IP provider did not return a valid address.");
        }

        root.TryGetProperty("connection", out JsonElement connection);
        root.TryGetProperty("timezone", out JsonElement timezone);
        string location = string.Join(" · ", new[] { Read(root, "country"), Read(root, "region"), Read(root, "city") }
            .Where(static part => part.Length > 0).Distinct(StringComparer.Ordinal));
        string asn = connection.ValueKind == JsonValueKind.Object
            && connection.TryGetProperty("asn", out JsonElement number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetInt64(out long value) && value >= 0
                ? $"AS{value}" : string.Empty;
        return new(address.ToString(), location, asn, Read(connection, "isp"), Read(connection, "org"), Read(timezone, "id"));
    }

    private static string Read(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.String) { return string.Empty; }
        return new string((value.GetString() ?? string.Empty).Where(static c => !char.IsControl(c)).Take(256).ToArray());
    }
}

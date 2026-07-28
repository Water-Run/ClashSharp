using System.Net.Http;

namespace ClashSharp.Infrastructure.Networking;

/// <summary>Reads an HTTP response status through a process-wide connection pool.</summary>
public sealed class HttpStatusProbe
{
    private static readonly HttpClient SharedClient = new();
    private readonly TimeSpan _timeout;

    /// <summary>Initializes a probe with a bounded per-request timeout.</summary>
    /// <param name="timeout">Positive request timeout.</param>
    public HttpStatusProbe(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        _timeout = timeout;
    }

    /// <summary>Returns the integer HTTP status code for one URI.</summary>
    /// <param name="uri">Absolute HTTP or HTTPS URI.</param>
    /// <param name="cancellationToken">Cancels the caller-owned request.</param>
    public async Task<int> GetStatusCodeAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using HttpResponseMessage response = await SharedClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        return (int)response.StatusCode;
    }
}

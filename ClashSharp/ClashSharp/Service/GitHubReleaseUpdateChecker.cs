using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ViewModel;

namespace ClashSharp.Service;

/// <summary>Checks the fixed ClashSharp GitHub latest-release endpoint without acquiring release assets.</summary>
/// <remarks>
/// Invariants: The request URI and repository are compile-time constants; response URLs are never consumed.
/// Thread safety: Concurrent checks are serialized so the in-memory ETag cache remains coherent.
/// Side effects: Issues a bounded HTTPS GET request. It never downloads or executes installer assets.
/// </remarks>
internal sealed class GitHubReleaseUpdateChecker : IApplicationUpdateChecker
{
    /// <summary>Fixed GitHub API endpoint for the ClashSharp repository.</summary>
    internal static readonly Uri LatestReleaseApiUri =
        new("https://api.github.com/repos/Water-Run/ClashSharp/releases/latest");

    /// <summary>Fixed human-facing release page opened by the application.</summary>
    internal static readonly Uri LatestReleasePageUri =
        new("https://github.com/Water-Run/ClashSharp/releases/latest");

    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _httpClient;
    private readonly StableVersion _currentVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ApplicationUpdateCheckResult? _cachedResult;
    private EntityTagHeaderValue? _entityTag;

    /// <summary>Initializes the checker with an injected HTTP client and installed version.</summary>
    /// <param name="httpClient">HTTP client configured with bounded timeout and redirect policy.</param>
    /// <param name="currentVersion">Installed application version.</param>
    public GitHubReleaseUpdateChecker(HttpClient httpClient, string currentVersion)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!StableVersion.TryParse(currentVersion, allowFourComponents: true, out _currentVersion))
        {
            throw new ArgumentException("The installed application version must be a stable numeric version.", nameof(currentVersion));
        }

        CurrentVersion = currentVersion;
    }

    /// <inheritdoc />
    public string CurrentVersion { get; }

    /// <summary>Creates the production HTTP client for the fixed GitHub API.</summary>
    /// <returns>A client with redirects disabled and a five-second timeout.</returns>
    internal static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    /// <inheritdoc />
    public async Task<ApplicationUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ApplicationUpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseApiUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClashSharp", _currentVersion.ToString()));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (_entityTag is not null)
            {
                request.Headers.IfNoneMatch.Add(_entityTag);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && _cachedResult is not null)
            {
                return _cachedResult;
            }

            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                return _cachedResult ?? ApplicationUpdateCheckResult.Unavailable();
            }

            byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            ApplicationUpdateCheckResult result = ParseResponse(payload);
            if (result.Availability == ApplicationUpdateAvailability.Unavailable)
            {
                return _cachedResult ?? result;
            }

            _cachedResult = result;
            _entityTag = response.Headers.ETag;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return _cachedResult ?? ApplicationUpdateCheckResult.Unavailable();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return _cachedResult ?? ApplicationUpdateCheckResult.Unavailable();
        }
    }

    private ApplicationUpdateCheckResult ParseResponse(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
            || (root.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.ValueKind == JsonValueKind.True)
            || !root.TryGetProperty("tag_name", out JsonElement tagNameElement)
            || tagNameElement.ValueKind != JsonValueKind.String)
        {
            return ApplicationUpdateCheckResult.Unavailable();
        }

        string? tagName = tagNameElement.GetString();
        if (!StableVersion.TryParse(tagName, allowFourComponents: false, out StableVersion latestVersion))
        {
            return ApplicationUpdateCheckResult.Unavailable();
        }

        return latestVersion.CompareTo(_currentVersion) > 0
            ? ApplicationUpdateCheckResult.UpdateAvailable(latestVersion.ToString())
            : ApplicationUpdateCheckResult.Current();
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new(capacity: Math.Min(
            MaximumResponseBytes,
            checked((int)(content.Headers.ContentLength ?? 0))));
        byte[] block = new byte[4096];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            total += read;
            if (total > MaximumResponseBytes)
            {
                throw new IOException("The GitHub release response exceeded the size limit.");
            }

            buffer.Write(block, 0, read);
        }
    }

    /// <summary>Numeric stable version used for deterministic comparison.</summary>
    internal readonly record struct StableVersion(int Major, int Minor, int Patch, int Revision)
        : IComparable<StableVersion>
    {
        public int CompareTo(StableVersion other)
        {
            int comparison = Major.CompareTo(other.Major);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Minor.CompareTo(other.Minor);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Patch.CompareTo(other.Patch);
            return comparison != 0 ? comparison : Revision.CompareTo(other.Revision);
        }

        public override string ToString()
        {
            return Revision == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
                : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}.{Revision}");
        }

        internal static bool TryParse(
            string? input,
            bool allowFourComponents,
            out StableVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            ReadOnlySpan<char> text = input.AsSpan().Trim();
            if (text[0] is 'v' or 'V')
            {
                text = text[1..];
            }

            int buildIndex = text.IndexOf('+');
            if (buildIndex >= 0)
            {
                if (buildIndex == text.Length - 1 || !IsValidBuildMetadata(text[(buildIndex + 1)..]))
                {
                    return false;
                }

                text = text[..buildIndex];
            }

            if (text.Contains('-'))
            {
                return false;
            }

            string[] components = text.ToString().Split('.', StringSplitOptions.None);
            if (components.Length != 3 && (!allowFourComponents || components.Length != 4))
            {
                return false;
            }

            Span<int> values = stackalloc int[4];
            for (int index = 0; index < components.Length; index++)
            {
                string component = components[index];
                if (component.Length == 0
                    || (component.Length > 1 && component[0] == '0')
                    || !int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
                {
                    return false;
                }
            }

            version = new StableVersion(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool IsValidBuildMetadata(ReadOnlySpan<char> metadata)
        {
            foreach (char character in metadata)
            {
                if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '.'))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

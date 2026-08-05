using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClashSharp.ServiceProtocol;
using Microsoft.Win32.SafeHandles;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.MihomoService;

/// <summary>Opaque service-side binding for one ready controller lifecycle epoch.</summary>
internal sealed record MihomoControllerRuntimeContext(
    Guid ServiceSessionId,
    long Generation,
    string ConfigurationHash,
    long LifecycleEpoch,
    int ProcessId,
    MihomoControllerAuthority Authority,
    MihomoServiceIpcEffectiveConfiguration EffectiveConfiguration);

/// <summary>Expected effective fields derived from one exact source generation.</summary>
internal sealed record MihomoRuntimeConfigurationPlan(
    int MixedPort,
    MihomoServiceIpcRoutingMode Mode,
    bool TunEnabled)
{
    private const long MaximumConfigurationBytes = 8L * 1024 * 1024;

    internal static async Task<MihomoRuntimeConfigurationPlan> ReadAsync(
        string configurationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        FileInfo file = new(configurationPath);
        file.Refresh();
        if (!file.Exists || file.Length > MaximumConfigurationBytes)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The readiness plan source is missing or exceeds its safety limit.");
        }

        string text;
        try
        {
            await using FileStream stream = new(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using StreamReader reader = new(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024,
                leaveOpen: false);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The readiness plan source is not valid UTF-8.",
                exception);
        }

        try
        {
            YamlStream yaml = new();
            using StringReader reader = new(text);
            yaml.Load(reader);
            if (yaml.Documents.Count != 1
                || yaml.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw InvalidPlan("The readiness plan requires one root mapping.");
            }

            Dictionary<string, YamlNode> rootItems = ReadUniqueMapping(root, "root");
            _ = ReadMixedPort(rootItems);
            MihomoServiceIpcRoutingMode mode = ReadMode(rootItems);
            bool tunEnabled = ReadTunEnabled(rootItems);
            // Service ownership is TUN-only; its unauthenticated loopback mixed listener is
            // deliberately disabled by the effective overlay.
            return new MihomoRuntimeConfigurationPlan(0, mode, tunEnabled);
        }
        catch (YamlException exception)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The readiness plan source is invalid YAML.",
                exception);
        }
    }

    private static int ReadMixedPort(IReadOnlyDictionary<string, YamlNode> root)
    {
        if (!root.TryGetValue("mixed-port", out YamlNode? node)
            || node is not YamlScalarNode scalar
            || !int.TryParse(
                scalar.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int port)
            || port is < 1 or > ushort.MaxValue)
        {
            throw InvalidPlan("The readiness plan requires a valid mixed-port.");
        }

        return port;
    }

    private static MihomoServiceIpcRoutingMode ReadMode(
        IReadOnlyDictionary<string, YamlNode> root)
    {
        if (!root.TryGetValue("mode", out YamlNode? node))
        {
            return MihomoServiceIpcRoutingMode.Rule;
        }

        if (node is not YamlScalarNode scalar || scalar.Value is null)
        {
            throw InvalidPlan("The readiness plan mode must be a scalar.");
        }

        return scalar.Value.ToLowerInvariant() switch
        {
            "direct" => MihomoServiceIpcRoutingMode.Direct,
            "rule" => MihomoServiceIpcRoutingMode.Rule,
            "global" => MihomoServiceIpcRoutingMode.Global,
            _ => throw InvalidPlan("The readiness plan mode is unsupported."),
        };
    }

    private static bool ReadTunEnabled(IReadOnlyDictionary<string, YamlNode> root)
    {
        if (!root.TryGetValue("tun", out YamlNode? tunNode))
        {
            return false;
        }

        if (tunNode is not YamlMappingNode tun)
        {
            throw InvalidPlan("The readiness plan tun section must be a mapping.");
        }

        Dictionary<string, YamlNode> fields = ReadUniqueMapping(tun, "tun");
        if (!fields.TryGetValue("enable", out YamlNode? enabledNode))
        {
            return false;
        }

        if (enabledNode is not YamlScalarNode scalar || scalar.Value is null)
        {
            throw InvalidPlan("The readiness plan tun.enable value must be a scalar.");
        }

        return scalar.Value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw InvalidPlan("The readiness plan tun.enable value is invalid."),
        };
    }

    private static Dictionary<string, YamlNode> ReadUniqueMapping(
        YamlMappingNode mapping,
        string description)
    {
        Dictionary<string, YamlNode> values = new(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: string key }
                || !values.TryAdd(key, valueNode))
            {
                throw InvalidPlan($"The readiness plan {description} mapping is ambiguous.");
            }
        }

        return values;
    }

    private static MihomoServiceConfigurationTrustException InvalidPlan(string message) =>
        new(message);
}

/// <summary>Bounded bytes returned by one service-private controller request.</summary>
internal sealed record MihomoControllerHttpResponse(
    HttpStatusCode StatusCode,
    ReadOnlyMemory<byte> Content);

/// <summary>Low-level HTTP-over-pipe transport used only by typed service operations.</summary>
internal interface IMihomoControllerTransport : IAsyncDisposable
{
    Task<MihomoControllerHttpResponse> SendAsync(
        HttpMethod method,
        string relativePath,
        ReadOnlyMemory<byte>? jsonContent,
        int maximumResponseBytes,
        CancellationToken cancellationToken);
}

internal interface IMihomoControllerTransportFactory
{
    IMihomoControllerTransport Create(MihomoControllerAuthority authority, int expectedProcessId);
}

/// <summary>
/// Sends HTTP/1.1 exclusively over a controller named pipe and authenticates its server process
/// before HttpClient is allowed to write the first request byte.
/// </summary>
internal sealed class MihomoNamedPipeControllerTransport : IMihomoControllerTransport
{
    private const string PipePrefix = @"\\.\pipe\";
    private const int CopyBufferBytes = 16 * 1024;
    private static readonly Uri BaseAddress = new("http://localhost/", UriKind.Absolute);

    internal const TokenImpersonationLevel ControllerImpersonationLevel =
        TokenImpersonationLevel.Anonymous;

    private readonly int _expectedProcessId;
    private readonly string _pipeName;
    private readonly string _controllerSecret;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;
    private int _disposed;

    internal MihomoNamedPipeControllerTransport(
        MihomoControllerAuthority authority,
        int expectedProcessId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedProcessId, 1);
        if (!authority.PipeName.StartsWith(PipePrefix, StringComparison.Ordinal)
            || authority.PipeName.Length <= PipePrefix.Length)
        {
            throw new ArgumentException("The controller pipe path is invalid.", nameof(authority));
        }

        _expectedProcessId = expectedProcessId;
        _pipeName = authority.PipeName[PipePrefix.Length..];
        if (_pipeName.Contains('\\') || _pipeName.Contains('/') || _pipeName.Any(char.IsControl))
        {
            throw new ArgumentException("The controller pipe name is invalid.", nameof(authority));
        }

        if (!IsCanonicalControllerSecret(authority.Secret))
        {
            throw new ArgumentException("The controller secret is invalid.", nameof(authority));
        }

        _controllerSecret = authority.Secret;

        _handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = ConnectPipeAsync,
            ConnectTimeout = Timeout.InfiniteTimeSpan,
            MaxConnectionsPerServer = 8,
            MaxResponseHeadersLength = 16,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            UseCookies = false,
            UseProxy = false,
        };
        _client = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<MihomoControllerHttpResponse> SendAsync(
        HttpMethod method,
        string relativePath,
        ReadOnlyMemory<byte>? jsonContent,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(method);
        ValidateRelativePath(relativePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResponseBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResponseBytes, 8 * 1024 * 1024);

        using HttpRequestMessage request = new(method, relativePath)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _controllerSecret);
        if (jsonContent is { } content)
        {
            if (content.Length > 64 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(jsonContent));
            }

            ByteArrayContent body = new(content.ToArray());
            body.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            request.Content = body;
        }

        using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Content.Headers.ContentEncoding.Count > 0)
        {
            throw new InvalidDataException("Encoded controller responses are not accepted.");
        }

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is < 0 || contentLength > maximumResponseBytes)
        {
            throw new InvalidDataException("The controller response exceeds its safety limit.");
        }

        ReadOnlyMemory<byte> bytes = await ReadBoundedAsync(
                response.Content,
                maximumResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return new MihomoControllerHttpResponse(response.StatusCode, bytes);
    }

    private async ValueTask<Stream> ConnectPipeAsync(
        SocketsHttpConnectionContext _,
        CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = CreateClientStream(_pipeName);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverProcessId))
            {
                throw new IOException(
                    "The controller pipe server identity could not be read.",
                    Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            }

            if (serverProcessId != (uint)_expectedProcessId)
            {
                throw new MihomoControllerServerIdentityException();
            }

            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static NamedPipeClientStream CreateClientStream(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return new NamedPipeClientStream(
            ".",
            pipeName,
            PipeAccessRights.ReadWrite,
            PipeOptions.Asynchronous,
            ControllerImpersonationLevel,
            HandleInheritability.None);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        ArrayBufferWriter<byte> writer = new(Math.Min(maximumBytes, CopyBufferBytes));
        while (true)
        {
            Memory<byte> destination = writer.GetMemory(
                Math.Min(CopyBufferBytes, maximumBytes + 1 - writer.WrittenCount));
            int bytesRead = await stream.ReadAsync(destination, cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return writer.WrittenMemory.ToArray();
            }

            writer.Advance(bytesRead);
            if (writer.WrittenCount > maximumBytes)
            {
                throw new InvalidDataException("The controller response exceeds its safety limit.");
            }
        }
    }

    private static bool IsCanonicalControllerSecret(string? secret)
    {
        if (secret is not { Length: 64 })
        {
            return false;
        }

        foreach (char character in secret)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        int queryIndex = relativePath.IndexOf('?');
        ReadOnlySpan<char> path = relativePath.AsSpan(
            0,
            queryIndex >= 0 ? queryIndex : relativePath.Length);
        if (relativePath[0] != '/'
            || relativePath.StartsWith("//", StringComparison.Ordinal)
            || relativePath.Contains('#')
            || relativePath.Contains('\\')
            || path.Contains(':')
            || relativePath.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException("The controller request path is invalid.", nameof(relativePath));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
            _handler.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}

internal sealed class MihomoNamedPipeControllerTransportFactory : IMihomoControllerTransportFactory
{
    public IMihomoControllerTransport Create(
        MihomoControllerAuthority authority,
        int expectedProcessId) =>
        new MihomoNamedPipeControllerTransport(authority, expectedProcessId);
}

internal sealed class MihomoControllerServerIdentityException : UnauthorizedAccessException
{
    internal MihomoControllerServerIdentityException()
        : base("The controller pipe belongs to an unexpected server process.")
    {
    }
}

internal sealed class MihomoControllerNotReadyException : IOException
{
    internal MihomoControllerNotReadyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal interface IMihomoControllerReadinessProbe
{
    Task<MihomoServiceIpcEffectiveConfiguration> WaitUntilReadyAsync(
        MihomoControllerAuthority authority,
        IMihomoChildProcess process,
        MihomoRuntimeConfigurationPlan expected,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Waits for the PID-authenticated controller and verifies its effective runtime plan.</summary>
internal sealed class MihomoControllerReadinessProbe : IMihomoControllerReadinessProbe
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly IMihomoControllerTransportFactory _transportFactory;

    internal MihomoControllerReadinessProbe(IMihomoControllerTransportFactory transportFactory)
    {
        _transportFactory = transportFactory
            ?? throw new ArgumentNullException(nameof(transportFactory));
    }

    public async Task<MihomoServiceIpcEffectiveConfiguration> WaitUntilReadyAsync(
        MihomoControllerAuthority authority,
        IMihomoChildProcess process,
        MihomoRuntimeConfigurationPlan expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(expected);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? lastFailure = null;
        await using IMihomoControllerTransport transport = _transportFactory.Create(
            authority,
            process.Id);
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new MihomoControllerNotReadyException(
                    "The child exited before its controller became ready.",
                    lastFailure);
            }

            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            attempt.CancelAfter(remaining < AttemptTimeout ? remaining : AttemptTimeout);
            try
            {
                await VerifyVersionAsync(transport, attempt.Token).ConfigureAwait(false);
                MihomoServiceIpcEffectiveConfiguration actual = await ReadConfigurationAsync(
                        transport,
                        attempt.Token)
                    .ConfigureAwait(false);
                if (actual.MixedPort == expected.MixedPort
                    && actual.Mode == expected.Mode
                    && actual.TunEnabled == expected.TunEnabled)
                {
                    return actual;
                }

                lastFailure = new InvalidDataException(
                    "The controller effective configuration does not match the source generation.");
            }
            catch (MihomoControllerServerIdentityException exception)
            {
                throw new MihomoControllerNotReadyException(
                    "The controller pipe failed server-process authentication.",
                    exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = new TimeoutException("A controller readiness attempt timed out.");
            }
            catch (Exception exception) when (exception is IOException
                or HttpRequestException
                or JsonException
                or InvalidOperationException)
            {
                lastFailure = exception;
            }

            remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                    remaining < RetryDelay ? remaining : RetryDelay,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new MihomoControllerNotReadyException(
            "The child controller did not become ready before the service deadline.",
            lastFailure);
    }

    private static async Task VerifyVersionAsync(
        IMihomoControllerTransport transport,
        CancellationToken cancellationToken)
    {
        MihomoControllerHttpResponse response = await transport.SendAsync(
                HttpMethod.Get,
                "/version",
                null,
                maximumResponseBytes: 4 * 1024,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException("The controller version probe returned an unexpected status.");
        }

        using JsonDocument document = JsonDocument.Parse(response.Content, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The controller version response is invalid.");
        }

        HashSet<string> propertyNames = new(StringComparer.Ordinal);
        string? version = null;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
            {
                throw new InvalidDataException("The controller version response is ambiguous.");
            }

            if (property.NameEquals("version"))
            {
                version = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
            }
        }

        if (string.IsNullOrWhiteSpace(version)
            || version.Length > 128
            || version.Any(char.IsControl))
        {
            throw new InvalidDataException("The controller version response is invalid.");
        }
    }

    private static async Task<MihomoServiceIpcEffectiveConfiguration> ReadConfigurationAsync(
        IMihomoControllerTransport transport,
        CancellationToken cancellationToken)
    {
        MihomoControllerHttpResponse response = await transport.SendAsync(
                HttpMethod.Get,
                "/configs",
                null,
                maximumResponseBytes: 256 * 1024,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException("The controller configuration probe returned an unexpected status.");
        }

        using JsonDocument document = JsonDocument.Parse(response.Content, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The controller configuration response is invalid.");
        }

        int? mixedPort = null;
        MihomoServiceIpcRoutingMode? mode = null;
        bool? tunEnabled = null;
        HashSet<string> propertyNames = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
            {
                throw new InvalidDataException("The controller configuration response is ambiguous.");
            }

            if (property.NameEquals("mixed-port"))
            {
                mixedPort = property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out int value)
                    && value is >= 0 and <= ushort.MaxValue
                        ? value
                        : null;
            }
            else if (property.NameEquals("mode"))
            {
                mode = ParseMode(property.Value);
            }
            else if (property.NameEquals("tun"))
            {
                tunEnabled = ParseTunEnabled(property.Value);
            }
        }

        if (mixedPort is null || mode is null || tunEnabled is null)
        {
            throw new InvalidDataException("The controller configuration projection is incomplete.");
        }

        return new MihomoServiceIpcEffectiveConfiguration
        {
            ControllerReady = true,
            MixedPort = mixedPort.Value,
            Mode = mode.Value,
            TunEnabled = tunEnabled.Value,
        };
    }

    private static MihomoServiceIpcRoutingMode? ParseMode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString()?.ToLowerInvariant() switch
        {
            "direct" => MihomoServiceIpcRoutingMode.Direct,
            "rule" => MihomoServiceIpcRoutingMode.Rule,
            "global" => MihomoServiceIpcRoutingMode.Global,
            _ => null,
        };
    }

    private static bool? ParseTunEnabled(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool? enabled = null;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException("The controller tun projection is ambiguous.");
            }

            if (property.NameEquals("enable"))
            {
                enabled = property.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }

        return enabled;
    }
}

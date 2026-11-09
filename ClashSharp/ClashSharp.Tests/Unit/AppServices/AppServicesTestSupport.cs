using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>Per-test scratch directory under the system temp folder; removed on dispose.</summary>
internal sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory(string? tag = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "clashsharp-appsvc-" + (tag ?? "t") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Yield();
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Yield();
            }
        }
    }
}

/// <summary>One request observed by <see cref="LoopbackHttpServer"/>.</summary>
internal sealed record RecordedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A real HTTP server bound to 127.0.0.1 only. It exists so transport behaviour (redirects, truncation,
/// stalls, compression, oversize) is exercised against a genuine socket without any network access.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentDictionary<Task, byte> _inflight = new();
    private readonly Task _acceptLoop;

    public LoopbackHttpServer(Func<HttpListenerContext, CancellationToken, Task> handler)
    {
        Handler = handler;
        int attempts = 0;
        while (true)
        {
            int port = FindFreePort();
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Port = port;
                break;
            }
            catch (HttpListenerException) when (++attempts < 25)
            {
                // The port was taken between probing and binding; probe again.
            }
        }

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Func<HttpListenerContext, CancellationToken, Task> Handler { get; set; }

    public int Port { get; }

    public string Url(string pathAndQuery = "/") => $"http://127.0.0.1:{Port}" + (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public CancellationToken Lifetime => _lifetime.Token;

    private static int FindFreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Task handling = Task.Run(() => HandleAsync(context));
            _inflight[handling] = 0;
            _ = handling.ContinueWith(task => _inflight.TryRemove(task, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in context.Request.Headers.AllKeys)
        {
            if (name is not null)
            {
                headers[name] = context.Request.Headers[name] ?? string.Empty;
            }
        }

        _requests.Enqueue(new RecordedRequest(
            context.Request.HttpMethod,
            context.Request.Url?.PathAndQuery ?? "/",
            headers));
        try
        {
            await Handler(context, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A client that gave up (timeout, cancellation) makes the response stream fail; that is expected.
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
                // The connection may already be aborted by the scenario.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Abort();
        try
        {
            await _acceptLoop.WaitAsync(TestTimeouts.Completion).ConfigureAwait(false);
            await Task.WhenAll(_inflight.Keys).WaitAsync(TestTimeouts.Completion).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Teardown only; a handler that ignores cancellation must not fail an unrelated assertion.
        }

        _lifetime.Dispose();
    }
}

/// <summary>Response builders for <see cref="LoopbackHttpServer"/> handlers.</summary>
internal static class Reply
{
    public static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task BytesAsync(
        HttpListenerContext context,
        int status,
        byte[] body,
        string? contentType = "text/plain; charset=utf-8",
        IEnumerable<(string Name, string Value)>? headers = null,
        bool chunked = false)
    {
        HttpListenerResponse response = context.Response;
        response.StatusCode = status;
        if (contentType is not null)
        {
            response.ContentType = contentType;
        }

        foreach ((string name, string value) in headers ?? [])
        {
            response.AddHeader(name, value);
        }

        if (chunked)
        {
            response.SendChunked = true;
        }
        else
        {
            response.ContentLength64 = body.Length;
        }

        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
    }

    public static Task TextAsync(
        HttpListenerContext context,
        int status,
        string body,
        string? contentType = "text/plain; charset=utf-8",
        IEnumerable<(string Name, string Value)>? headers = null,
        bool chunked = false) =>
        BytesAsync(context, status, Utf8NoBom.GetBytes(body), contentType, headers, chunked);

    public static Task RedirectAsync(HttpListenerContext context, int status, string location)
    {
        context.Response.StatusCode = status;
        context.Response.RedirectLocation = location;
        context.Response.ContentLength64 = 0;
        return Task.CompletedTask;
    }

    /// <summary>Promises <paramref name="declaredLength"/> bytes, sends <paramref name="sent"/>, then drops the connection.</summary>
    public static async Task TruncatedAsync(HttpListenerContext context, long declaredLength, byte[] sent)
    {
        HttpListenerResponse response = context.Response;
        response.StatusCode = 200;
        response.ContentLength64 = declaredLength;
        await response.OutputStream.WriteAsync(sent).ConfigureAwait(false);
        await response.OutputStream.FlushAsync().ConfigureAwait(false);
        response.Abort();
    }
}

/// <summary>Fakes for the effects behind <see cref="ProfileCatalogService"/>; none touches the real machine.</summary>
internal sealed class CatalogSourceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

    public string? Get(string profileId)
    {
        lock (_gate)
        {
            return _sources.TryGetValue(profileId, out string? text) ? text : null;
        }
    }

    private Action? _beforeNextSet;

    /// <summary>Runs once, immediately before the next source write; lets a test arm a fault at the commit boundary.</summary>
    public Action? BeforeNextSet
    {
        get => Volatile.Read(ref _beforeNextSet);
        set => Volatile.Write(ref _beforeNextSet, value);
    }

    public void Set(string profileId, string text)
    {
        Action? hook = Interlocked.Exchange(ref _beforeNextSet, null);
        hook?.Invoke();
        lock (_gate)
        {
            _sources[profileId] = text;
        }
    }

    public bool Remove(string profileId)
    {
        lock (_gate)
        {
            return _sources.Remove(profileId);
        }
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_sources, StringComparer.Ordinal);
        }
    }
}

internal sealed class CatalogSettingsFake : IProfileCatalogSettings
{
    public string ActiveProfileId { get; set; } = string.Empty;

    public Task SetActiveProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActiveProfileId = profileId;
        return Task.CompletedTask;
    }
}

internal sealed class CatalogCoreFake(CatalogSourceStore sources) : IProfileCatalogCoreConfiguration
{
    private int _importCalls;

    public int ImportCalls => Volatile.Read(ref _importCalls);

    /// <summary>Called with the 1-based import number; may throw to inject a failure at the core boundary.</summary>
    public Func<int, string, Task>? BeforeImport { get; set; }

    public async Task<ProfileImportResult> ImportProfileConfigurationAsync(
        string profileId,
        string profileName,
        string configurationText,
        CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _importCalls);
        if (BeforeImport is not null)
        {
            await BeforeImport(call, profileId).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        sources.Set(profileId, configurationText);
        return new ProfileImportResult(profileId, profileName, profileId + ".yaml", 2, 3, "validated");
    }

    public Task<string?> ReadImportedProfileConfigurationAsync(string profileId, CancellationToken cancellationToken) =>
        Task.FromResult(sources.Get(profileId));

    public CoreConfigurationState EnsureDefaultConfiguration() => throw new NotSupportedException();

    public Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class CatalogRuntimeFake(CatalogSourceStore sources) : IProfileCatalogRuntime
{
    public string? ObservedActiveProfileId { get; set; }

    public bool ApplyResult { get; set; } = true;

    public Exception? DeleteException { get; set; }

    public List<string> Applied { get; } = [];

    public List<string> Deleted { get; } = [];

    public string ReadInstalledProfileId(string configuredProfileId) => ObservedActiveProfileId ?? configuredProfileId;

    public Task<string> ReadActiveProfileIdAsync(string configuredProfileId, CancellationToken cancellationToken) =>
        Task.FromResult(ObservedActiveProfileId ?? configuredProfileId);

    public Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        Applied.Add(profileId);
        return Task.FromResult(ApplyResult);
    }

    /// <summary>Called before an active-profile import reaches the (fake) live runtime; may throw to inject a failure.</summary>
    public Func<Task>? BeforeImportAndApply { get; set; }

    public async Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(
        string profileId,
        string profileName,
        string configurationText,
        CancellationToken cancellationToken)
    {
        if (BeforeImportAndApply is not null)
        {
            await BeforeImportAndApply().ConfigureAwait(false);
        }

        if (ApplyResult)
        {
            sources.Set(profileId, configurationText);
        }

        return new ProfileCatalogRuntimeImportResult(
            new ProfileImportResult(profileId, profileName, profileId + ".yaml", 2, 3, "validated"),
            ApplyResult);
    }

    public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        if (DeleteException is { } exception)
        {
            throw exception;
        }

        Deleted.Add(profileId);
        return Task.FromResult(sources.Remove(profileId));
    }
}

internal sealed class CatalogLogFake : IProfileCatalogLog
{
    private readonly ConcurrentQueue<(string Level, string Message)> _entries = new();

    public IReadOnlyList<(string Level, string Message)> Entries => [.. _entries];

    public void AppendLog(string level, string category, string message, string? detail) => _entries.Enqueue((level, message));
}

/// <summary>A catalog service wired to fakes inside a scratch directory.</summary>
internal sealed class CatalogHarness : IAsyncDisposable
{
    public CatalogHarness(TimeSpan? downloadTimeout = null, Func<string, string>? getString = null)
    {
        Directory = new ScratchDirectory("catalog");
        Sources = new CatalogSourceStore();
        Settings = new CatalogSettingsFake();
        Core = new CatalogCoreFake(Sources);
        Runtime = new CatalogRuntimeFake(Sources);
        Log = new CatalogLogFake();
        Service = Open(downloadTimeout, getString);
    }

    public ScratchDirectory Directory { get; }

    public CatalogSourceStore Sources { get; }

    public CatalogSettingsFake Settings { get; }

    public CatalogCoreFake Core { get; }

    public CatalogRuntimeFake Runtime { get; }

    public CatalogLogFake Log { get; }

    public ProfileCatalogService Service { get; private set; }

    public string CatalogPath => Directory.Combine("ProfileCatalog.json");

    public string HistoryRoot => Directory.Combine("mihomo", "history");

    public ProfileCatalogService Open(TimeSpan? downloadTimeout = null, Func<string, string>? getString = null) =>
        new(
            CatalogPath,
            HistoryRoot,
            Settings,
            Core,
            Runtime,
            Log,
            getString ?? (static key => key),
            UncoordinatedProfileCatalogMutationCoordinator.Instance,
            downloadTimeout);

    /// <summary>Retires the current service and opens a fresh one over the same files (a process restart).</summary>
    public async Task ReopenAsync(TimeSpan? downloadTimeout = null)
    {
        await Service.DisposeAsync();
        Service = Open(downloadTimeout);
    }

    /// <summary>Files left in the history tree (excluding directories).</summary>
    public string[] HistoryFiles() =>
        System.IO.Directory.Exists(HistoryRoot)
            ? [.. System.IO.Directory.EnumerateFiles(HistoryRoot, "*", SearchOption.AllDirectories)]
            : [];

    public string[] PendingHistoryFiles() =>
        System.IO.Directory.Exists(Path.Combine(HistoryRoot, ".pending"))
            ? [.. System.IO.Directory.EnumerateFiles(Path.Combine(HistoryRoot, ".pending"))]
            : [];

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Directory.Dispose();
    }
}

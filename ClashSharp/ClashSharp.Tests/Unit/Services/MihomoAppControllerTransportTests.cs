using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class MihomoAppControllerTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConnectAsync_WrongServerPid_ClosesBeforeAnyHttpBytes()
    {
        await AssertRejectedBeforeHttpBytesAsync(
            new FakeIdentitySource(IsCurrent: true),
            new FakeTcpOwnerVerifier(ConnectedOwnerMatches: false));
    }

    [Fact]
    public async Task ConnectAsync_ChangedEpoch_ClosesBeforeAnyHttpBytes()
    {
        await AssertRejectedBeforeHttpBytesAsync(
            new FakeIdentitySource(IsCurrent: false),
            new FakeTcpOwnerVerifier(ConnectedOwnerMatches: true));
    }

    [Fact]
    public async Task ConnectAsync_CanceledDuringOwnerRetry_ClosesBeforeAnyHttpBytes()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            FakeTcpOwnerVerifier verifier = new(ConnectedOwnerMatches: false);
            MihomoAppControllerTransport transport = new(
                new FakeIdentitySource(IsCurrent: true),
                verifier,
                ownerVerificationAttempts: 8,
                ownerVerificationRetryDelay: TimeSpan.FromSeconds(1));
            using CancellationTokenSource cancellation = new();
            using HttpRequestMessage request = new(HttpMethod.Get, "/version");
            SocketsHttpConnectionContext context = CreateConnectionContext(
                new DnsEndPoint("127.0.0.1", port, AddressFamily.InterNetwork),
                request);

            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            Task<Stream> connectTask = transport.ConnectAsync(context, cancellation.Token)
                .AsTask();
            using Socket server = await acceptTask.WaitAsync(TestTimeout);
            await verifier.FirstConnectedCheck.WaitAsync(TestTimeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
            byte[] buffer = new byte[1];
            int bytesRead = await server.ReceiveAsync(buffer, SocketFlags.None)
                .WaitAsync(TestTimeout);

            Assert.Equal(0, bytesRead);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectAsync_ExactOwnerAndEpoch_AllowsHttpRequest()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            FakeIdentitySource identitySource = new(IsCurrent: true);
            FakeTcpOwnerVerifier verifier = new(ConnectedOwnerMatches: true);
            MihomoAppControllerTransport transport = CreateTransport(identitySource, verifier);
            using SocketsHttpHandler handler = CreateHandler(transport);
            using HttpClient client = new(handler)
            {
                Timeout = TestTimeout,
            };

            Task<string> serverTask = ServeOneResponseAsync(listener);
            using HttpResponseMessage response = await client.GetAsync(
                    $"http://127.0.0.1:{port}/version",
                    CancellationToken.None)
                .WaitAsync(TestTimeout);
            string request = await serverTask.WaitAsync(TestTimeout);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("GET /version HTTP/1.1\r\n", request, StringComparison.Ordinal);
            Assert.Equal(identitySource.Identity.RootProcessId, verifier.LastExpectedConnectedPid);
            Assert.Equal(1, identitySource.CurrentChecks);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectAsync_NonExactLoopbackHost_NeverOpensSocket()
    {
        FakeIdentitySource identitySource = new(IsCurrent: true);
        FakeTcpOwnerVerifier verifier = new(ConnectedOwnerMatches: true);
        MihomoAppControllerTransport transport = CreateTransport(identitySource, verifier);
        using SocketsHttpHandler handler = CreateHandler(transport);
        using HttpClient client = new(handler)
        {
            Timeout = TestTimeout,
        };

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://localhost:9090/version"));

        Assert.Contains(
            "exact IPv4 loopback",
            failure.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(0, identitySource.Captures);
        Assert.Equal(0, verifier.ConnectedChecks);
    }

    [Fact]
    public void IsLoopbackListenerOwnedBy_RevalidatesEpochBeforeAndAfterOwnerQuery()
    {
        FakeIdentitySource identitySource = new(
            IsCurrent: true,
            CurrentResults: [true, false]);
        FakeTcpOwnerVerifier verifier = new(
            ConnectedOwnerMatches: true,
            ListenerOwnerMatches: true);
        MihomoAppControllerTransport transport = CreateTransport(identitySource, verifier);

        bool owned = transport.IsLoopbackListenerOwnedBy(7890, identitySource.Identity);

        Assert.False(owned);
        Assert.Equal(2, identitySource.CurrentChecks);
        Assert.Equal(1, verifier.ListenerChecks);
        Assert.Equal(identitySource.Identity.RootProcessId, verifier.LastExpectedListenerPid);
    }

    [Fact]
    public void Capture_InvalidIdentity_FailsClosed()
    {
        FakeIdentitySource identitySource = new(
            IsCurrent: true,
            Identity: new MihomoAppProcessIdentity(Guid.Empty, 0));
        MihomoAppControllerTransport transport = CreateTransport(
            identitySource,
            new FakeTcpOwnerVerifier(ConnectedOwnerMatches: true));

        Assert.Null(transport.Capture());
    }

    private static async Task AssertRejectedBeforeHttpBytesAsync(
        FakeIdentitySource identitySource,
        FakeTcpOwnerVerifier verifier)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            MihomoAppControllerTransport transport = CreateTransport(identitySource, verifier);
            using SocketsHttpHandler handler = CreateHandler(transport);
            using HttpClient client = new(handler)
            {
                Timeout = TestTimeout,
            };

            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            Task<HttpResponseMessage> requestTask = client.GetAsync(
                $"http://127.0.0.1:{port}/version",
                CancellationToken.None);
            using Socket server = await acceptTask.WaitAsync(TestTimeout);
            HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
                () => requestTask);
            byte[] buffer = new byte[1];
            int bytesRead = await server.ReceiveAsync(buffer, SocketFlags.None)
                .WaitAsync(TestTimeout);

            Assert.Contains(
                "server identity could not be authenticated",
                failure.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(0, bytesRead);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static MihomoAppControllerTransport CreateTransport(
        IMihomoAppProcessIdentitySource identitySource,
        IWindowsTcpOwnerVerifier verifier) =>
        new(
            identitySource,
            verifier,
            ownerVerificationAttempts: 1,
            ownerVerificationRetryDelay: TimeSpan.Zero);

    private static SocketsHttpHandler CreateHandler(MihomoAppControllerTransport transport) =>
        new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = transport.ConnectAsync,
            PooledConnectionLifetime = TimeSpan.Zero,
            UseCookies = false,
            UseProxy = false,
        };

    private static SocketsHttpConnectionContext CreateConnectionContext(
        DnsEndPoint endpoint,
        HttpRequestMessage request)
    {
        ConstructorInfo constructor = typeof(SocketsHttpConnectionContext).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(DnsEndPoint), typeof(HttpRequestMessage)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "The framework HTTP connection context constructor was not found.");
        return (SocketsHttpConnectionContext)constructor.Invoke([endpoint, request]);
    }

    private static async Task<string> ServeOneResponseAsync(TcpListener listener)
    {
        using Socket server = await listener.AcceptSocketAsync(CancellationToken.None)
            .ConfigureAwait(false);
        byte[] buffer = new byte[4096];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await server.ReceiveAsync(
                    buffer.AsMemory(count),
                    SocketFlags.None,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            count += read;
            if (Encoding.ASCII.GetString(buffer, 0, count)
                .Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        string request = Encoding.ASCII.GetString(buffer, 0, count);
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        await server.SendAsync(response, SocketFlags.None, CancellationToken.None)
            .ConfigureAwait(false);
        return request;
    }

    private sealed class FakeIdentitySource : IMihomoAppProcessIdentitySource
    {
        private readonly Queue<bool>? _currentResults;
        private readonly bool _isCurrent;

        internal FakeIdentitySource(
            bool IsCurrent,
            MihomoAppProcessIdentity? Identity = null,
            IEnumerable<bool>? CurrentResults = null)
        {
            _isCurrent = IsCurrent;
            this.Identity = Identity
                ?? new MihomoAppProcessIdentity(Guid.NewGuid(), Environment.ProcessId);
            _currentResults = CurrentResults is null ? null : new Queue<bool>(CurrentResults);
        }

        internal MihomoAppProcessIdentity Identity { get; }

        internal int Captures { get; private set; }

        internal int CurrentChecks { get; private set; }

        public MihomoAppProcessIdentity? CaptureCurrent()
        {
            Captures++;
            return Identity;
        }

        public bool IsStillCurrent(MihomoAppProcessIdentity identity)
        {
            CurrentChecks++;
            return identity == Identity
                && (_currentResults is { Count: > 0 }
                    ? _currentResults.Dequeue()
                    : _isCurrent);
        }
    }

    private sealed class FakeTcpOwnerVerifier : IWindowsTcpOwnerVerifier
    {
        private readonly bool _connectedOwnerMatches;
        private readonly bool _listenerOwnerMatches;

        internal FakeTcpOwnerVerifier(
            bool ConnectedOwnerMatches,
            bool ListenerOwnerMatches = false)
        {
            _connectedOwnerMatches = ConnectedOwnerMatches;
            _listenerOwnerMatches = ListenerOwnerMatches;
        }

        internal int ConnectedChecks { get; private set; }

        internal int ListenerChecks { get; private set; }

        internal int LastExpectedConnectedPid { get; private set; }

        internal int LastExpectedListenerPid { get; private set; }

        internal Task FirstConnectedCheck => _firstConnectedCheck.Task;

        private readonly TaskCompletionSource _firstConnectedCheck = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsConnectedServerOwnedBy(Socket connectedClient, int expectedPid)
        {
            ConnectedChecks++;
            LastExpectedConnectedPid = expectedPid;
            _firstConnectedCheck.TrySetResult();
            return _connectedOwnerMatches;
        }

        public bool IsLoopbackListenerOwnedBy(int port, int expectedPid)
        {
            ListenerChecks++;
            LastExpectedListenerPid = expectedPid;
            return _listenerOwnerMatches;
        }
    }
}

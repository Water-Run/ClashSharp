using System.Net;
using System.Net.Sockets;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class WindowsTcpOwnerVerifierTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0x00005000u, 80)]
    [InlineData(0x0000BB01u, 443)]
    [InlineData(0xCAFE8223u, 9090)]
    public void DecodePort_UsesNetworkByteOrderAndIgnoresUpperBits(
        uint encodedPort,
        int expectedPort)
    {
        Assert.Equal(expectedPort, WindowsTcpOwnerVerifier.DecodePort(encodedPort));
    }

    [Fact]
    public async Task NativeVerifier_MatchesExactEstablishedServerTupleAndPid()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using Socket client = new(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);
            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            await client.ConnectAsync(IPAddress.Loopback, port, CancellationToken.None)
                .AsTask()
                .WaitAsync(TestTimeout);
            using Socket server = await acceptTask.WaitAsync(TestTimeout);

            bool exactOwner = await WaitForResultAsync(
                () => WindowsTcpOwnerVerifier.Instance.IsConnectedServerOwnedBy(
                    client,
                    Environment.ProcessId));

            Assert.True(exactOwner);
            Assert.False(WindowsTcpOwnerVerifier.Instance.IsConnectedServerOwnedBy(
                client,
                DifferentPid));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task NativeVerifier_MatchesExactLoopbackListenerAndFailsClosedAfterRemoval()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            bool exactOwner = await WaitForResultAsync(
                () => WindowsTcpOwnerVerifier.Instance.IsLoopbackListenerOwnedBy(
                    port,
                    Environment.ProcessId));

            Assert.True(exactOwner);
            Assert.False(WindowsTcpOwnerVerifier.Instance.IsLoopbackListenerOwnedBy(
                port,
                DifferentPid));
        }
        finally
        {
            listener.Stop();
        }

        bool stillPresent = await WaitForResultAsync(
            () => !WindowsTcpOwnerVerifier.Instance.IsLoopbackListenerOwnedBy(
                port,
                Environment.ProcessId));
        Assert.True(stillPresent);
    }

    [Fact]
    public void NativeVerifier_InvalidOrMissingEndpoints_FailClosed()
    {
        using Socket disconnected = new(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp);

        Assert.False(WindowsTcpOwnerVerifier.Instance.IsConnectedServerOwnedBy(
            disconnected,
            Environment.ProcessId));
        Assert.False(WindowsTcpOwnerVerifier.Instance.IsLoopbackListenerOwnedBy(
            0,
            Environment.ProcessId));
        Assert.False(WindowsTcpOwnerVerifier.Instance.IsLoopbackListenerOwnedBy(
            65_536,
            Environment.ProcessId));
    }

    private static int DifferentPid => Environment.ProcessId == int.MaxValue
        ? int.MaxValue - 1
        : Environment.ProcessId + 1;

    private static async Task<bool> WaitForResultAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TestTimeout);
        do
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
        while (!timeout.IsCancellationRequested);

        return false;
    }
}

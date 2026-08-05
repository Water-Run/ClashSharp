using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Service;

/// <summary>Immutable identity for one App-owned mihomo root-process generation.</summary>
internal readonly record struct MihomoAppProcessIdentity(Guid Epoch, int RootProcessId);

/// <summary>Captures and revalidates the exact App-owned mihomo process generation.</summary>
internal interface IMihomoAppProcessIdentitySource
{
    /// <summary>Captures the current generation, or null when the App owns no live generation.</summary>
    MihomoAppProcessIdentity? CaptureCurrent();

    /// <summary>Checks that an earlier capture still identifies the current live generation.</summary>
    bool IsStillCurrent(MihomoAppProcessIdentity identity);
}

/// <summary>
/// Opens IPv4 loopback controller connections only after binding the server endpoint to the exact
/// App-owned mihomo process generation.
/// </summary>
internal sealed class MihomoAppControllerTransport
{
    private const int DefaultOwnerVerificationAttempts = 8;
    private static readonly TimeSpan DefaultOwnerVerificationRetryDelay =
        TimeSpan.FromMilliseconds(10);

    private readonly IMihomoAppProcessIdentitySource _identitySource;
    private readonly IWindowsTcpOwnerVerifier _ownerVerifier;
    private readonly int _ownerVerificationAttempts;
    private readonly TimeSpan _ownerVerificationRetryDelay;

    /// <summary>Initializes the production transport with a bounded short owner-table retry.</summary>
    internal MihomoAppControllerTransport(
        IMihomoAppProcessIdentitySource identitySource,
        IWindowsTcpOwnerVerifier ownerVerifier)
        : this(
            identitySource,
            ownerVerifier,
            DefaultOwnerVerificationAttempts,
            DefaultOwnerVerificationRetryDelay)
    {
    }

    /// <summary>Initializes the transport with deterministic retry inputs for isolated tests.</summary>
    internal MihomoAppControllerTransport(
        IMihomoAppProcessIdentitySource identitySource,
        IWindowsTcpOwnerVerifier ownerVerifier,
        int ownerVerificationAttempts,
        TimeSpan ownerVerificationRetryDelay)
    {
        _identitySource = identitySource ?? throw new ArgumentNullException(nameof(identitySource));
        _ownerVerifier = ownerVerifier ?? throw new ArgumentNullException(nameof(ownerVerifier));
        ArgumentOutOfRangeException.ThrowIfLessThan(ownerVerificationAttempts, 1);
        if (ownerVerificationRetryDelay < TimeSpan.Zero
            || ownerVerificationRetryDelay > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ownerVerificationRetryDelay));
        }

        _ownerVerificationAttempts = ownerVerificationAttempts;
        _ownerVerificationRetryDelay = ownerVerificationRetryDelay;
    }

    /// <summary>Captures a valid current App-owned process generation for a readiness transaction.</summary>
    internal MihomoAppProcessIdentity? Capture()
    {
        MihomoAppProcessIdentity? identity = _identitySource.CaptureCurrent();
        return identity is { } captured
            && captured.Epoch != Guid.Empty
            && captured.RootProcessId > 0
                ? captured
                : null;
    }

    /// <summary>Revalidates a previously captured generation at readiness commit time.</summary>
    internal bool IsStillCurrent(MihomoAppProcessIdentity identity) =>
        identity.Epoch != Guid.Empty
        && identity.RootProcessId > 0
        && _identitySource.IsStillCurrent(identity);

    /// <summary>Checks the exact loopback listener against a previously captured root process.</summary>
    internal bool IsLoopbackListenerOwnedBy(int port, MihomoAppProcessIdentity identity) =>
        identity.Epoch != Guid.Empty
        && identity.RootProcessId > 0
        && IsStillCurrent(identity)
        && _ownerVerifier.IsLoopbackListenerOwnedBy(port, identity.RootProcessId)
        && IsStillCurrent(identity);

    /// <summary>
    /// Connects an HTTP socket after exact server PID authentication and epoch revalidation.
    /// </summary>
    /// <remarks>
    /// This method is intended for <see cref="SocketsHttpHandler.ConnectCallback"/>. Callers must
    /// prevent pooled connections from surviving a process-generation transition.
    /// </remarks>
    internal async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        DnsEndPoint target = context.DnsEndPoint;
        if (target.AddressFamily is not (AddressFamily.Unspecified or AddressFamily.InterNetwork)
            || !string.Equals(target.Host, "127.0.0.1", StringComparison.Ordinal)
            || target.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new MihomoAppControllerIdentityException(
                "The App-owned mihomo controller target is not exact IPv4 loopback.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        MihomoAppProcessIdentity identity = Capture()
            ?? throw new MihomoAppControllerIdentityException(
                "No current App-owned mihomo process generation is available.");

        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(
                    new IPEndPoint(IPAddress.Loopback, target.Port),
                    cancellationToken)
                .ConfigureAwait(false);

            bool exactOwner = false;
            for (int attempt = 0; attempt < _ownerVerificationAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_ownerVerifier.IsConnectedServerOwnedBy(socket, identity.RootProcessId))
                {
                    exactOwner = true;
                    break;
                }

                if (attempt + 1 < _ownerVerificationAttempts
                    && _ownerVerificationRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_ownerVerificationRetryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!exactOwner || !IsStillCurrent(identity))
            {
                throw new MihomoAppControllerIdentityException(
                    "The App-owned mihomo controller server identity could not be authenticated.");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>Signals that the App-owned controller endpoint did not match its process generation.</summary>
internal sealed class MihomoAppControllerIdentityException : UnauthorizedAccessException
{
    internal MihomoAppControllerIdentityException(string message)
        : base(message)
    {
    }
}

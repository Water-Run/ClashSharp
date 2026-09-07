using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal sealed record WindowsOwnerTransferParentHandoff(
    InstallerTransactionSnapshot Continuation, WindowsMachineHelperBroker Broker);

/// <summary>
/// Owns elevation, endpoint authentication and explicit confirmation. Success transfers the same
/// process, signed-image pin and pipe into the ordinary broker. Failure closes the pipe and awaits
/// the helper's actual exit before releasing its pinned resources; no helper work is abandoned.
/// </summary>
internal sealed class WindowsOwnerTransferBroker
{
    private readonly string _executablePath;
    private readonly InstallerReleaseManifest _manifest;
    private readonly string _targetSid;
    private readonly IWindowsInstallerExecutableTrustVerifier _trust;
    private readonly IWindowsOwnerTransferServerFactory _servers;
    private readonly IWindowsOwnerTransferProcessLauncher _launcher;
    private readonly Func<WindowsMachineHelperBroker> _ordinaryBroker;
    private readonly Func<int> _processId;
    private readonly WindowsMachineHelperBrokerLimits _limits;

    internal WindowsOwnerTransferBroker(string executablePath, InstallerReleaseManifest manifest, string targetSid,
        IWindowsInstallerExecutableTrustVerifier trust, IWindowsOwnerTransferServerFactory servers,
        IWindowsOwnerTransferProcessLauncher launcher, Func<WindowsMachineHelperBroker> ordinaryBroker,
        Func<int> processId, WindowsMachineHelperBrokerLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(ordinaryBroker);
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(limits);
        manifest.Validate();
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        limits.Validate();
        _executablePath = executablePath;
        _manifest = manifest;
        _targetSid = targetSid;
        _trust = trust;
        _servers = servers;
        _launcher = launcher;
        _ordinaryBroker = ordinaryBroker;
        _processId = processId;
        _limits = limits;
    }

    internal static WindowsOwnerTransferBroker CreateDefault(string executablePath, InstallerReleaseManifest manifest, string targetSid) =>
        new(executablePath, manifest, targetSid, new WindowsInstallerExecutableTrustVerifier(manifest),
            new WindowsOwnerTransferTransportFactory(), new WindowsRunAsProcessLauncher(),
            () => WindowsMachineHelperBroker.CreateDefault(executablePath, manifest),
            static () => Environment.ProcessId, WindowsMachineHelperBrokerLimits.Default);

    internal async Task<WindowsOwnerTransferParentHandoff> StartAsync(InstallerOperation operation,
        Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        cancellationToken.ThrowIfCancellationRequested();
        var bootstrap = InstallerOwnerTransferBootstrap.Create(_manifest.InstallerPayloadSha256, _processId());
        var request = new InstallerOwnerTransferRequest(bootstrap.SessionId, operation,
            _manifest.ExpectedPackageVersion, _manifest.InstallerPayloadSha256);
        InstallerRequest ordinaryRequest = request.ForAuthenticatedAccount(_targetSid);
        IWindowsMachineHelperServer? server = _servers.Create(bootstrap);
        IWindowsInstallerExecutableTrustLease? trust = null;
        IWindowsElevatedHelperProcess? process = null;
        WindowsMachineHelperBroker? broker = null;
        try
        {
            using var elevation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            elevation.CancelAfter(_limits.ElevationTimeout);
            trust = await _trust.VerifyAsync(_executablePath, elevation.Token).ConfigureAwait(false);
            process = await _launcher.StartAsync(trust.ExecutablePath, bootstrap, elevation.Token).ConfigureAwait(false);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connection.CancelAfter(_limits.ConnectionTimeout);
            await server.WaitForConnectionAsync(connection.Token).ConfigureAwait(false);
            server.VerifyClient(process.ProcessId);
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exchange.CancelAfter(_limits.CommandTimeout);
            await InstallerOwnerTransferFraming.WriteRequestAsync(server.Transport, request, exchange.Token).ConfigureAwait(false);
            InstallerOwnerTransferOffer offer = await InstallerOwnerTransferFraming.ReadOfferAsync(server.Transport, exchange.Token).ConfigureAwait(false);
            offer.ValidateAgainst(request, _targetSid);
            ordinaryRequest = offer.Request.ForAuthenticatedAccount(_targetSid);
            bool accepted = await confirm(offer, exchange.Token).ConfigureAwait(false);
            await InstallerOwnerTransferFraming.WriteDecisionAsync(server.Transport,
                new(bootstrap.SessionId, offer.OfferId, accepted), exchange.Token).ConfigureAwait(false);
            if (!accepted)
            {
                throw new InstallerUserCancelledException("installer.owner_transfer.declined");
            }
            InstallerMachineHelperCommand ready = await InstallerMachineHelperFraming.ReadCommandAsync(
                server.Transport, exchange.Token).ConfigureAwait(false);
            InstallerTransactionSnapshot continuation = ready.ToDurableState();
            if (ready.Verb != InstallerMachineHelperVerb.Prepare || continuation.Journal.Phase != InstallerTransactionPhase.Prepared
                || !continuation.Journal.Matches(ordinaryRequest))
            {
                throw new InstallerProtocolException("installer.owner_transfer.handoff_invalid");
            }
            broker = _ordinaryBroker();
            broker.Adopt(continuation, server, process, trust);
            server = null;
            process = null;
            trust = null;
            WindowsMachineHelperBroker adopted = broker;
            broker = null;
            return new(continuation, adopted);
        }
        finally
        {
            try
            {
                if (broker is not null)
                {
                    await broker.DisposeAsync().ConfigureAwait(false);
                }
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (process is not null)
                    {
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    try { process?.Dispose(); }
                    finally { trust?.Dispose(); }
                }
            }
        }
    }
}

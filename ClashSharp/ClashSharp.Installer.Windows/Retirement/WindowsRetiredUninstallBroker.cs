using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Retirement;

internal sealed record WindowsRetiredUninstallParentHandoff(
    InstallerTransactionSnapshot Ready, WindowsMachineHelperBroker Broker);

/// <summary>
/// Owns elevation and endpoint authentication after explicit account-copy removal confirmation. Success transfers the same
/// process, signed-image pin and pipe into the ordinary broker. Failure closes the pipe and awaits
/// the helper's actual exit before releasing its pinned resources; no helper work is abandoned.
/// </summary>
internal sealed class WindowsRetiredUninstallBroker
{
    private readonly string _executablePath;
    private readonly InstallerReleaseManifest _manifest;
    private readonly string _targetSid;
    private readonly IWindowsInstallerExecutableTrustVerifier _trust;
    private readonly IWindowsRetiredUninstallServerFactory _servers;
    private readonly IWindowsRetiredUninstallProcessLauncher _launcher;
    private readonly Func<WindowsMachineHelperBroker> _ordinaryBroker;
    private readonly Func<int> _processId;
    private readonly WindowsMachineHelperBrokerLimits _limits;

    internal WindowsRetiredUninstallBroker(string executablePath, InstallerReleaseManifest manifest, string targetSid,
        IWindowsInstallerExecutableTrustVerifier trust, IWindowsRetiredUninstallServerFactory servers,
        IWindowsRetiredUninstallProcessLauncher launcher, Func<WindowsMachineHelperBroker> ordinaryBroker,
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

    internal static WindowsRetiredUninstallBroker CreateDefault(string executablePath, InstallerReleaseManifest manifest, string targetSid) =>
        new(executablePath, manifest, targetSid, new WindowsInstallerExecutableTrustVerifier(manifest),
            new WindowsRetiredUninstallTransportFactory(), new WindowsRunAsProcessLauncher(),
            () => WindowsMachineHelperBroker.CreateDefault(executablePath, manifest),
            static () => Environment.ProcessId, WindowsMachineHelperBrokerLimits.Default);

    internal async Task<WindowsRetiredUninstallParentHandoff> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bootstrap = InstallerRetiredUninstallBootstrap.Create(_manifest.InstallerPayloadSha256, _processId());
        var request = new InstallerRequest(InstallerOperation.Uninstall, _targetSid, false,
            _manifest.ExpectedPackageVersion, _manifest.InstallerPayloadSha256);
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
            await InstallerRetiredUninstallProtocol.WriteAsync(server.Transport,
                InstallerTransactionSnapshot.Create(InstallerTransactionJournal.Create(request)), exchange.Token).ConfigureAwait(false);
            InstallerTransactionSnapshot continuation = await InstallerRetiredUninstallProtocol.ReadReadyAsync(
                server.Transport, exchange.Token).ConfigureAwait(false);
            InstallerRetiredUninstallProtocol.ValidateAgainst(continuation, bootstrap, request);
            broker = _ordinaryBroker();
            broker.AdoptRetiredUninstall(continuation, server, process, trust);
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

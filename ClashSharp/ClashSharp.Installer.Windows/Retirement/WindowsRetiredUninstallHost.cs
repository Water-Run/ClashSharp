using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Retirement;

/// <summary>
/// Authenticates the signed self image, parent PID/SID and pipe endpoints before opening private
/// state. Recovery is read only by the helper; the parent receives bounded public transaction state.
/// </summary>
internal sealed class WindowsRetiredUninstallHost(
    string executablePath, InstallerReleaseManifest manifest, IWindowsMachineHelperElevationVerifier elevation,
    IWindowsInstallerExecutableTrustVerifier trustVerifier, IWindowsMachineHelperParentProcessVerifier parentVerifier,
    IWindowsRetiredUninstallClientFactory clients, IWindowsRetiredUninstallAuthorityFactory authorityFactory,
    WindowsMachineHelperHostLimits limits)
{
    internal async Task RunAsync(InstallerRetiredUninstallBootstrap bootstrap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        manifest.Validate();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        elevation.VerifyElevated();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connection.CancelAfter(limits.ConnectionTimeout);
        using IWindowsInstallerExecutableTrustLease trust = await trustVerifier.VerifyAsync(executablePath, connection.Token).ConfigureAwait(false);
        using IWindowsMachineHelperParentProcessLease parent = parentVerifier.Acquire(bootstrap.ParentProcessId, trust.ExecutablePath);
        await using IWindowsMachineHelperClient client = clients.Create(bootstrap);
        await client.ConnectAsync(connection.Token).ConfigureAwait(false);
        client.VerifyServer(parent.ProcessId);
        parent.VerifyAlive();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.SessionTimeout);
        var request = new InstallerRequest(InstallerOperation.Uninstall, parent.UserSid, false,
            manifest.ExpectedPackageVersion, manifest.InstallerPayloadSha256);
        InstallerTransactionSnapshot proposed = await InstallerRetiredUninstallProtocol.ReadAsync(client.Transport, deadline.Token).ConfigureAwait(false);
        InstallerRetiredUninstallProtocol.ValidateAgainst(proposed, bootstrap, request);
        if (proposed.Journal.Phase != InstallerTransactionPhase.Prepared)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.proposal_invalid");
        }
        parent.VerifyAlive();
        WindowsRetiredUninstallHandoff handoff;
        try
        {
            handoff = await authorityFactory.CreateAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (InstallerProtocolException exception)
        {
            await InstallerRetiredUninstallProtocol.WriteFailureAsync(client.Transport, exception.DiagnosticCode, deadline.Token).ConfigureAwait(false);
            return;
        }
        await using IWindowsMachineHelperAuthorityLease authority = handoff.Authority;
        InstallerRetiredUninstallProtocol.ValidateAgainst(handoff.Ready, bootstrap, request);
        parent.VerifyAlive();
        await InstallerRetiredUninstallProtocol.WriteAsync(client.Transport, handoff.Ready, deadline.Token).ConfigureAwait(false);
        InstallerMachineHelperCommand first = await InstallerMachineHelperFraming.ReadCommandAsync(client.Transport, deadline.Token).ConfigureAwait(false);
        if (first.ToDurableState() != handoff.Ready || first.ToInvocation() != InstallerRetiredUninstallProtocol.FirstInvocation(handoff.Ready))
        {
            throw new InstallerProtocolException("installer.retired_uninstall.continuation_changed");
        }
        await InstallerMachineHelperAuthorityLoop.RunAsync(client.Transport, authority.Session, first, deadline.Token).ConfigureAwait(false);
    }
}

using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsOwnerTransferOfferSource
{
    /// <summary>Inspects without mutation and keeps all private evidence inside this helper.</summary>
    Task<WindowsOwnerTransferConfirmedState> CaptureAsync(InstallerOwnerTransferRequest request,
        string authenticatedTargetSid, CancellationToken cancellationToken);
}

/// <summary>
/// Authenticates the dedicated process endpoints, captures a read-only offer and requires explicit
/// consent before obtaining mutation authority. The successful ordinary session retains that authority
/// and both authenticated process leases on this same pipe until every command has drained.
/// </summary>
internal sealed class WindowsOwnerTransferHost
{
    private readonly string _executablePath;
    private readonly IWindowsMachineHelperElevationVerifier _elevation;
    private readonly IWindowsInstallerExecutableTrustVerifier _trust;
    private readonly IWindowsMachineHelperParentProcessVerifier _parent;
    private readonly IWindowsOwnerTransferClientFactory _clients;
    private readonly IWindowsOwnerTransferOfferSource _offers;
    private readonly IWindowsOwnerTransferAuthorityFactory _authority;
    private readonly WindowsMachineHelperHostLimits _limits;

    internal WindowsOwnerTransferHost(string executablePath, IWindowsMachineHelperElevationVerifier elevation,
        IWindowsInstallerExecutableTrustVerifier trust, IWindowsMachineHelperParentProcessVerifier parent,
        IWindowsOwnerTransferClientFactory clients, IWindowsOwnerTransferOfferSource offers,
        IWindowsOwnerTransferAuthorityFactory authority, WindowsMachineHelperHostLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        _executablePath = executablePath;
        _elevation = elevation;
        _trust = trust;
        _parent = parent;
        _clients = clients;
        _offers = offers;
        _authority = authority;
        _limits = limits;
    }

    internal async Task RunAsync(InstallerOwnerTransferBootstrap bootstrap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        _elevation.VerifyElevated();
        using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionDeadline.CancelAfter(_limits.ConnectionTimeout);
        using IWindowsInstallerExecutableTrustLease trust = await _trust.VerifyAsync(
            _executablePath, connectionDeadline.Token).ConfigureAwait(false);
        using IWindowsMachineHelperParentProcessLease parent = _parent.Acquire(bootstrap.ParentProcessId, trust.ExecutablePath);
        await using IWindowsMachineHelperClient client = _clients.Create(bootstrap);
        await client.ConnectAsync(connectionDeadline.Token).ConfigureAwait(false);
        client.VerifyServer(parent.ProcessId);
        parent.VerifyAlive();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limits.SessionTimeout);
        InstallerOwnerTransferRequest request = await InstallerOwnerTransferFraming.ReadRequestAsync(
            client.Transport, deadline.Token).ConfigureAwait(false);
        request.ValidateAgainst(bootstrap);
        WindowsOwnerTransferConfirmedState captured = await _offers.CaptureAsync(
            request, parent.UserSid, deadline.Token).ConfigureAwait(false);
        captured.Validate(parent.UserSid);
        InstallerOwnerTransferRequest offeredRequest = captured.ExpectedPrivateState is null
            ? request : request with { Operation = captured.Journal.Continuation.Operation };
        if (!captured.Journal.Continuation.Matches(offeredRequest.ForAuthenticatedAccount(parent.UserSid)))
        {
            throw new InstallerProtocolException("installer.owner_transfer.offer_mismatch");
        }
        var offer = new InstallerOwnerTransferOffer(offeredRequest, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            captured.Journal.PreviousOwner.Association.OwnerSid, parent.UserSid, captured.ExpectedPrivateState is not null);
        await InstallerOwnerTransferFraming.WriteOfferAsync(client.Transport, offer, deadline.Token).ConfigureAwait(false);
        InstallerOwnerTransferDecision decision = await InstallerOwnerTransferFraming.ReadDecisionAsync(
            client.Transport, deadline.Token).ConfigureAwait(false);
        decision.ValidateAgainst(offer);
        if (!decision.Accepted)
        {
            return;
        }
        parent.VerifyAlive();
        WindowsOwnerTransferHandoff handoff = await _authority.CreateAsync(captured, parent.UserSid, deadline.Token).ConfigureAwait(false);
        await using IWindowsMachineHelperAuthorityLease authority = handoff.Authority;
        InstallerTransactionSnapshot expected = InstallerTransactionSnapshot.Create(captured.Journal.Continuation);
        if (handoff.Continuation != expected)
        {
            throw new InstallerProtocolException("installer.owner_transfer.continuation_changed");
        }
        parent.VerifyAlive();
        var invocation = InstallerMachineHelperInvocation.Create(InstallerMachineHelperVerb.Prepare, expected);
        // This contains only ordinary public transaction fields, never the private participants.
        await InstallerMachineHelperFraming.WriteCommandAsync(client.Transport,
            InstallerMachineHelperCommand.Create(invocation, expected), deadline.Token).ConfigureAwait(false);
        InstallerMachineHelperCommand first = await InstallerMachineHelperFraming.ReadCommandAsync(
            client.Transport, deadline.Token).ConfigureAwait(false);
        if (first.ToInvocation() != invocation || first.ToDurableState() != expected)
        {
            throw new InstallerProtocolException("installer.owner_transfer.continuation_changed");
        }
        await InstallerMachineHelperAuthorityLoop.RunAsync(client.Transport, authority.Session, first, deadline.Token).ConfigureAwait(false);
    }
}

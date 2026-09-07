using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Publishes the exact ordinary Prepared barrier only after private Prepared is durable and both
/// App lifetime barriers are held. The supplied store remains guarded for the previous owner.
/// Unknown Installer children or conflicting ordinary state are never adopted or removed.
/// </summary>
internal sealed class WindowsOwnerTransferStartupBlock
{
    private readonly IInstallerReleaseLease _release;
    private readonly IWindowsOwnerTransferStateBackend _backend;
    private readonly IWindowsOwnerTransferAccessNative _directories;
    private readonly IWindowsOwnerTransferCertificateStateReader _certificates;
    private readonly IInstallerTransactionStore _ordinary;

    internal WindowsOwnerTransferStartupBlock(IInstallerReleaseLease release,
        IWindowsOwnerTransferStateBackend backend, IWindowsOwnerTransferAccessNative directories,
        IWindowsOwnerTransferCertificateStateReader certificates, IInstallerTransactionStore ordinary)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(ordinary);
        _release = release;
        _backend = backend;
        _directories = directories;
        _certificates = certificates;
        _ordinary = ordinary;
    }

    internal async Task ApplyAndVerifyAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        if (current.Phase != InstallerOwnerTransferPhase.Prepared)
        {
            throw new InstallerProtocolException("installer.owner_transfer.startup_phase_invalid");
        }
        cancellationToken.ThrowIfCancellationRequested();
        InstallerRequest request = WindowsOwnerTransferDeployment.CreateContinuationRequest(current);
        try
        {
            await _release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (current.NextCertificateLedger is { } next && !next.Matches(request, _release.Release))
            {
                throw new InstallerProtocolException("installer.owner_transfer.certificate_release_conflict");
            }
            WindowsMachineDeploymentPlan deployment = WindowsOwnerTransferDeployment.ResolveNextPlan(
                current, _release.Manifest, _backend, cancellationToken);
            var plan = new WindowsOwnerTransferCertificatePlan(deployment.Roots, current);
            using WindowsOwnerTransferAccessTree tree =
                WindowsOwnerTransferAccessTree.AcquireForStartupBlock(deployment.Roots, current, _directories, cancellationToken);
            await VerifyInitialStateAsync(tree, plan, cancellationToken).ConfigureAwait(false);
            InstallerTransactionSnapshot expected = InstallerTransactionSnapshot.Create(current.Continuation);
            InstallerTransactionSnapshot? observed = await _ordinary.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (observed is null)
            {
                await SaveAndReconcileAsync(expected, tree, cancellationToken).ConfigureAwait(false);
            }
            else if (observed != expected)
            {
                throw new InstallerProtocolException("installer.owner_transfer.continuation_changed");
            }
            tree.PinStartupContinuation(current.Continuation, cancellationToken);
            await _release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (WindowsOwnerTransferDeployment.ResolveNextPlan(current, _release.Manifest, _backend, cancellationToken).Roots != deployment.Roots)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_plan_changed");
            }
            if (await _ordinary.LoadAsync(cancellationToken).ConfigureAwait(false) != expected)
            {
                throw new InstallerProtocolException("installer.owner_transfer.continuation_changed");
            }
            await VerifyInitialStateAsync(tree, plan, cancellationToken).ConfigureAwait(false);
            tree.VerifyContinuation(current.Continuation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.owner_transfer.startup_failed");
        }
    }

    private async Task SaveAndReconcileAsync(InstallerTransactionSnapshot expected,
        WindowsOwnerTransferAccessTree tree, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = null;
        try
        {
            await _ordinary.SaveAsync(expected.Journal, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            failure = exception;
        }
        InstallerTransactionSnapshot? observed;
        try
        {
            tree.Reverify(requireTransferred: true, CancellationToken.None);
            observed = await _ordinary.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (observed == expected)
            {
                tree.PinStartupContinuation(expected.Journal, CancellationToken.None);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerStateUncertainException("installer.owner_transfer.startup_state_uncertain");
        }
        if (observed == expected)
        {
            return;
        }
        if (observed is null && failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        throw new InstallerStateUncertainException("installer.owner_transfer.startup_state_uncertain");
    }

    private async Task VerifyInitialStateAsync(WindowsOwnerTransferAccessTree tree,
        WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken)
    {
        tree.Reverify(requireTransferred: true, cancellationToken);
        InstallerOwnerTransferCertificateState state = await _certificates.ReadAsync(plan, cancellationToken).ConfigureAwait(false);
        if (state != new InstallerOwnerTransferCertificateState(plan.Journal.PreviousCertificateLedger, null, plan.Journal.NextCertificateLedger))
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_state_conflict");
        }
        tree.VerifyAssociation(plan.Journal.PreviousOwner.Association);
        tree.Reverify(requireTransferred: true, cancellationToken);
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException or UnauthorizedAccessException
        or Win32Exception or InstallerProtocolException or InstallerStateUncertainException or OperationCanceledException;
}

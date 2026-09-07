using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Composes the six native mutation boundaries and final observer under the caller's continuous
/// authenticated authority. It never changes either journal or reacquires machine/App exclusion.
/// </summary>
internal sealed class WindowsOwnerTransferPhaseExecutor : IInstallerOwnerTransferPhaseExecutor
{
    private readonly WindowsOwnerTransferStartupBlock _startup;
    private readonly WindowsOwnerTransferServiceRemoval _service;
    private readonly WindowsOwnerTransferMachineAccess _machineAccess;
    private readonly WindowsOwnerTransferAssociation _association;
    private readonly WindowsOwnerTransferCertificates _certificates;
    private readonly WindowsOwnerTransferInstallerAccess _installerAccess;
    private readonly WindowsOwnerTransferCompletion _completion;

    internal WindowsOwnerTransferPhaseExecutor(IInstallerReleaseLease release, IInstallerTransactionStore ordinary,
        IWindowsOwnerTransferServiceBackend backend, IWindowsOwnerTransferAccessNative directories,
        IWindowsOwnerTransferAssociationFileNative association, IWindowsOwnerTransferCertificateFileNative certificates)
    {
        _startup = new(release, backend, directories, certificates, ordinary);
        _service = new(release, ordinary, backend);
        _machineAccess = new(release, backend, directories);
        _association = new(release, backend, directories, association);
        _certificates = new(release, backend, directories, certificates);
        _installerAccess = new(release, backend, directories, certificates);
        _completion = new(release, backend, directories, certificates);
    }

    public Task ApplyAndVerifyAsync(InstallerOwnerTransferJournal current,
        InstallerOwnerTransferPhase nextPhase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        if (nextPhase <= InstallerOwnerTransferPhase.Prepared
            || nextPhase > InstallerOwnerTransferPhase.InstallerAccessTransferred
            || (int)nextPhase != (int)current.Phase + 1)
        {
            throw new InstallerProtocolException("installer.owner_transfer.executor_phase_invalid");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return nextPhase switch
        {
            InstallerOwnerTransferPhase.StartupBlocked => _startup.ApplyAndVerifyAsync(current, cancellationToken),
            InstallerOwnerTransferPhase.PreviousServiceRemoved => _service.ApplyAndVerifyAsync(current, cancellationToken),
            InstallerOwnerTransferPhase.MachineAccessTransferred => _machineAccess.ApplyAndVerifyAsync(current, cancellationToken),
            InstallerOwnerTransferPhase.AssociationTransferred => _association.ApplyAndVerifyAsync(current, cancellationToken),
            InstallerOwnerTransferPhase.CertificateStateTransferred => _certificates.ApplyAndVerifyAsync(current, cancellationToken),
            InstallerOwnerTransferPhase.InstallerAccessTransferred => _installerAccess.ApplyAndVerifyAsync(current, cancellationToken),
            _ => throw new InstallerProtocolException("installer.owner_transfer.executor_phase_invalid"),
        };
    }

    public Task VerifyCompletedAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken) =>
        _completion.VerifyAsync(current, cancellationToken);
}

using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsOwnerTransferAuthorityResources : IDisposable
{
    IInstallerOwnerTransferStore Store { get; }
    IInstallerOwnerTransferPhaseExecutor Executor { get; }
    Task VerifyNewPreparationAsync(WindowsMachineDeploymentRoots roots,
        InstallerOwnerTransferJournal journal, CancellationToken cancellationToken);
}

internal interface IWindowsOwnerTransferAuthorityResourcesFactory
{
    IWindowsOwnerTransferAuthorityResources Create(
        InstallerOwnerTransferJournal journal, IInstallerReleaseLease release, bool newTransfer);
}

/// <summary>Creates lazy store/phase adapters only inside the confirmed helper's exclusive scope.</summary>
internal sealed class WindowsOwnerTransferAuthorityResourcesFactory : IWindowsOwnerTransferAuthorityResourcesFactory
{
    private readonly IWindowsOwnerTransferServiceBackend _backend;

    internal WindowsOwnerTransferAuthorityResourcesFactory(IWindowsOwnerTransferServiceBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    public IWindowsOwnerTransferAuthorityResources Create(
        InstallerOwnerTransferJournal journal, IInstallerReleaseLease release, bool newTransfer) =>
        WindowsOwnerTransferAuthorityResources.CreateDefault(journal, release, _backend, newTransfer);
}

internal sealed class WindowsOwnerTransferAuthorityResources : IWindowsOwnerTransferAuthorityResources
{
    private readonly WindowsInstallerOwnerTransferPersistence _privatePersistence;
    private readonly WindowsInstallerTransactionRootGuard _ordinaryRoot;
    private readonly FileInstallerTransactionStore _ordinary;
    private readonly WindowsOwnerTransferPreparationVerifier _preparation;
    private bool _disposed;

    private WindowsOwnerTransferAuthorityResources(WindowsInstallerOwnerTransferPersistence privatePersistence,
        WindowsInstallerTransactionRootGuard ordinaryRoot, FileInstallerTransactionStore ordinary,
        IInstallerOwnerTransferPhaseExecutor executor, WindowsOwnerTransferPreparationVerifier preparation)
    {
        _privatePersistence = privatePersistence;
        _ordinaryRoot = ordinaryRoot;
        _ordinary = ordinary;
        Store = new InstallerOwnerTransferStore(privatePersistence);
        Executor = executor;
        _preparation = preparation;
    }

    public IInstallerOwnerTransferStore Store { get; }
    public IInstallerOwnerTransferPhaseExecutor Executor { get; }

    internal static WindowsOwnerTransferAuthorityResources CreateDefault(InstallerOwnerTransferJournal journal,
        IInstallerReleaseLease release, IWindowsOwnerTransferServiceBackend backend, bool newTransfer)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(backend);
        journal.Validate();
        WindowsInstallerOwnerTransferPersistence persistence = newTransfer
            ? WindowsInstallerOwnerTransferPersistence.CreateDefault()
            : WindowsInstallerOwnerTransferPersistence.CreateForRecoveryDefault();
        WindowsInstallerTransactionRootGuard? ordinaryRoot = null;
        FileInstallerTransactionStore? ordinary = null;
        try
        {
            // This guard is used only in Prepared/StartupBlocked. Later replay never opens old
            // Installer roots through it, and disposal does not revalidate their transferred ACLs.
            ordinaryRoot = WindowsInstallerTransactionRootGuard.CreateReadOnlyDefault(journal.PreviousOwner.Association.OwnerSid);
            ordinary = new FileInstallerTransactionStore(ordinaryRoot.RootPath, ordinaryRoot);
            var directories = new WindowsOwnerTransferAccessNative();
            var certificates = new WindowsOwnerTransferCertificateFileNative();
            return new(persistence, ordinaryRoot, ordinary,
                new WindowsOwnerTransferPhaseExecutor(release, ordinary, backend, directories,
                    new WindowsOwnerTransferAssociationFileNative(), certificates),
                new WindowsOwnerTransferPreparationVerifier(directories, certificates, ordinary));
        }
        catch
        {
            ordinary?.Dispose();
            ordinaryRoot?.Dispose();
            persistence.Dispose();
            throw;
        }
    }

    public Task VerifyNewPreparationAsync(WindowsMachineDeploymentRoots roots,
        InstallerOwnerTransferJournal journal, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _preparation.VerifyAsync(new(roots, journal), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _ordinary.Dispose();
        }
        finally
        {
            try
            {
                _ordinaryRoot.Dispose();
            }
            finally
            {
                _privatePersistence.Dispose();
            }
        }
    }
}

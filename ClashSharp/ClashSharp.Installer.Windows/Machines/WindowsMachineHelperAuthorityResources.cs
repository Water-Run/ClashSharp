using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperAuthorityResources : IAsyncDisposable
{
    IInstallerTransactionStore TransactionStore { get; }

    IInstallerMachineHelperOperationExecutor Operations { get; }

    Task<InstallerDirectoryCleanupReport> CompleteUninstallAsync(
        InstallerTransactionSnapshot verified, CancellationToken cancellationToken);
}

internal interface IWindowsMachineHelperAuthorityResourcesFactory
{
    IWindowsMachineHelperAuthorityResources Create(string targetSid);
}

/// <summary>
/// Defers protected-store creation until the authenticated first command has proven its target SID.
/// </summary>
internal sealed class WindowsMachineHelperAuthorityResourcesFactory
    : IWindowsMachineHelperAuthorityResourcesFactory
{
    private readonly Func<
        IInstallerCertificateOwnershipStore,
        IInstallerMachineHelperOperationExecutor> _operationsFactory;

    internal WindowsMachineHelperAuthorityResourcesFactory(
        Func<IInstallerCertificateOwnershipStore, IInstallerMachineHelperOperationExecutor>
            operationsFactory)
    {
        ArgumentNullException.ThrowIfNull(operationsFactory);
        _operationsFactory = operationsFactory;
    }

    public IWindowsMachineHelperAuthorityResources Create(string targetSid) =>
        WindowsMachineHelperAuthorityResources.CreateDefault(targetSid, _operationsFactory);
}

internal sealed class WindowsMachineHelperAuthorityResources
    : IWindowsMachineHelperAuthorityResources
{
    private readonly WindowsInstallerProtectedStateStores _stores;
    private readonly IInstallerMachineHelperOperationExecutor _operations;
    private readonly WindowsInstallerEmptyDirectoryFinalizer _directoryFinalizer;
    private bool _disposed;

    internal WindowsMachineHelperAuthorityResources(
        WindowsInstallerProtectedStateStores stores,
        IInstallerMachineHelperOperationExecutor operations,
        WindowsInstallerEmptyDirectoryFinalizer directoryFinalizer)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(directoryFinalizer);
        _stores = stores;
        _operations = operations;
        _directoryFinalizer = directoryFinalizer;
    }

    public IInstallerTransactionStore TransactionStore => _stores.Transactions;

    public IInstallerMachineHelperOperationExecutor Operations => _operations;

    public Task<InstallerDirectoryCleanupReport> CompleteUninstallAsync(
        InstallerTransactionSnapshot verified, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _directoryFinalizer.CompleteAsync(verified, DisposeAsync, cancellationToken);
    }

    internal static WindowsMachineHelperAuthorityResources CreateDefault(
        string targetSid,
        Func<IInstallerCertificateOwnershipStore, IInstallerMachineHelperOperationExecutor>
            operationsFactory)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        ArgumentNullException.ThrowIfNull(operationsFactory);
        WindowsInstallerProtectedStateStores stores =
            WindowsInstallerProtectedStateStores.CreateDefault(targetSid);
        try
        {
            IInstallerMachineHelperOperationExecutor operations =
                operationsFactory(stores.CertificateOwnership)
                ?? throw new InstallerProtocolException(
                    "installer.machine_helper.operations_missing");
            return new WindowsMachineHelperAuthorityResources(stores, operations,
                WindowsInstallerEmptyDirectoryFinalizer.CreateDefault());
        }
        catch
        {
            stores.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_operations is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_operations is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            _stores.Dispose();
            _disposed = true;
        }
    }
}

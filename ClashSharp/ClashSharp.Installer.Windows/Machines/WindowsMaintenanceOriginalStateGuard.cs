using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMaintenanceOriginalContentsReader
{
    Task<WindowsMaintenanceOriginalContents> ReadAsync(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// Retained authenticated machine/App exclusion and original-installation path/file leases.
/// The coordinator must hold this capability through every SCM effect and terminal reconciliation.
/// </summary>
internal interface IWindowsMaintenanceRecoveryAuthorityLease
{
    Task ReverifyAsync(CancellationToken cancellationToken);
}

/// <summary>Rechecks complete original evidence and the still-uncommitted protected transaction.</summary>
internal sealed class WindowsMaintenanceOriginalStateGuard : IWindowsServiceRestorationGuard
{
    private readonly WindowsServicePreparationBaseline _service;
    private readonly WindowsMaintenanceOriginalContents _contents;
    private readonly IWindowsMaintenanceOriginalContentsReader _reader;
    private readonly IInstallerTransactionReader _transactions;
    private readonly IWindowsMaintenanceRecoveryAuthorityLease _authority;

    internal WindowsMaintenanceOriginalStateGuard(WindowsServicePreparationBaseline service, WindowsMaintenanceOriginalContents contents,
        IWindowsMaintenanceOriginalContentsReader reader, IInstallerTransactionReader transactions, IWindowsMaintenanceRecoveryAuthorityLease authority)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _contents = contents ?? throw new ArgumentNullException(nameof(contents));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async Task VerifyOriginalStateAsync(WindowsMachineDeploymentPlan plan, WindowsServicePreparationBaseline baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseline);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] expected = _service.Serialize();
        byte[]? supplied = null;
        try
        {
            supplied = baseline.Serialize();
            if (!expected.AsSpan().SequenceEqual(supplied))
            {
                throw new InstallerProtocolException("installer.recovery.service_baseline_mismatch");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            if (supplied is not null) { CryptographicOperations.ZeroMemory(supplied); }
        }
        await _authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
        InstallerTransactionSnapshot before = await ReadTransactionAsync(plan, baseline, cancellationToken).ConfigureAwait(false);
        _contents.RequirePlan(plan);
        WindowsMaintenanceOriginalContents actual = await _reader.ReadAsync(plan, cancellationToken).ConfigureAwait(false)
            ?? throw new InstallerProtocolException("installer.recovery.original_contents_missing");
        actual.RequirePlan(plan);
        if (!_contents.Matches(actual))
        {
            throw new InstallerProtocolException("installer.recovery.original_state_changed");
        }
        InstallerTransactionSnapshot after = await ReadTransactionAsync(plan, baseline, cancellationToken).ConfigureAwait(false);
        if (before != after)
        {
            throw new InstallerProtocolException("installer.recovery.transaction_changed");
        }
        await _authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstallerTransactionSnapshot> ReadTransactionAsync(WindowsMachineDeploymentPlan plan,
        WindowsServicePreparationBaseline baseline, CancellationToken cancellationToken)
    {
        InstallerTransactionSnapshot snapshot = await _transactions.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InstallerProtocolException("installer.recovery.transaction_missing");
        snapshot.Validate();
        baseline.RequireBoundary(plan, snapshot.Journal);
        return snapshot;
    }
}

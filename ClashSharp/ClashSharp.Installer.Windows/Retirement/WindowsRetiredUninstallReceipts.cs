using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Retirement;

/// <summary>
/// The unelevated account cannot read another owner's ordinary journal or private recovery files.
/// This reader advances only from validated receipts on the dedicated authenticated pipe. After an
/// uncertain exchange it cannot claim any state; a new helper must read private recovery evidence.
/// </summary>
internal sealed class WindowsRetiredUninstallReceipts : IWindowsMachineHelperBroker, IInstallerTransactionReader,
    IInstallerCertificateMutation
{
    private readonly IWindowsMachineHelperBroker _broker;
    private InstallerTransactionSnapshot? _state;
    private bool _uncertain;
    private int _active;

    internal WindowsRetiredUninstallReceipts(InstallerTransactionSnapshot ready, IWindowsMachineHelperBroker broker)
    {
        InstallerRetiredUninstallProtocol.Validate(ready);
        ArgumentNullException.ThrowIfNull(broker);
        _state = ready;
        _broker = broker;
    }

    public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_uncertain || Volatile.Read(ref _active) != 0)
        {
            throw new InstallerStateUncertainException("installer.retired_uninstall.receipt_unavailable");
        }
        return Task.FromResult(_state);
    }

    public async Task<InstallerMachineHelperResult> ExecuteAsync(InstallerMachineHelperCommand command)
    {
        InstallerRetiredUninstallProtocol.Validate(command.ToDurableState());
        if (_uncertain || command.ToDurableState() != _state)
        {
            throw new InstallerStateUncertainException("installer.retired_uninstall.receipt_unavailable");
        }
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.concurrent_access");
        }
        try
        {
            InstallerMachineHelperResult result = await _broker.ExecuteAsync(command).ConfigureAwait(false);
            InstallerTransactionSnapshot committed = result.ValidateAgainst(command);
            InstallerRetiredUninstallProtocol.Validate(committed);
            _state = command.Verb == InstallerMachineHelperVerb.Clear && result.Outcome == InstallerMachineHelperOutcome.Succeeded
                ? null : committed;
            return result;
        }
        catch
        {
            _uncertain = true;
            throw;
        }
        finally { Volatile.Write(ref _active, 0); }
    }

    public async Task ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        InstallerTransactionSnapshot? receipt = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (receipt is null || !receipt.Journal.Matches(request)
            || receipt.Journal.Phase is not (InstallerTransactionPhase.PackageCommitted or InstallerTransactionPhase.Verified))
        {
            throw new InstallerProtocolException("installer.retired_uninstall.certificate_receipt_missing");
        }
        // The dedicated helper only acknowledges PackageCommitted after exact archived trust
        // cleanup. Final Verify and Clear independently repeat those checks before success.
    }
}

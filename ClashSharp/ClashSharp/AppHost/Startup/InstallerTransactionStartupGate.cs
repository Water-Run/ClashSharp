using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Stops startup read-only when the Installer has retained a public transaction marker.</summary>
internal sealed class InstallerTransactionStartupGate(
    InstallerTransactionState state,
    MutationAdmissionBarrier mutationAdmission) : IStartupStep
{
    internal const string PendingDiagnosticCode = "installer.transaction.pending";
    internal const string InvalidDiagnosticCode = "installer.transaction.state_invalid";

    public string Name => "installer-transaction-gate";

    public int Order => 125;

    public async Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (state == InstallerTransactionState.Clear)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StartupStepResult.Succeeded();
        }

        await using MutationAdmissionLease shutdownLease = await mutationAdmission
            .CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, cancellationToken)
            .ConfigureAwait(false);
        return StartupStepResult.Fatal(state == InstallerTransactionState.Pending
            ? PendingDiagnosticCode
            : InvalidDiagnosticCode);
    }

    /// <summary>Returns whether a startup diagnostic requires the Installer Repair-only prompt.</summary>
    internal static bool IsBlockingDiagnosticCode(string diagnosticCode)
    {
        return StringComparer.Ordinal.Equals(diagnosticCode, PendingDiagnosticCode)
            || StringComparer.Ordinal.Equals(diagnosticCode, InvalidDiagnosticCode);
    }
}

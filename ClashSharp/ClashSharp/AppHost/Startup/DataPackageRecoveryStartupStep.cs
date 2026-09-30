using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Resolves a retained import/reset generation before settings drive startup effects.</summary>
internal sealed class DataPackageRecoveryStartupStep(
    ClashDataPackageService dataPackages,
    MutationAdmissionBarrier mutationAdmission,
    LogStorageService logStorage) : IStartupStep
{
    public string Name => "data-package-recovery";

    public int Order => 50;

    public async Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using MutationAdmissionLease recoveryLease = await mutationAdmission
                .CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken)
                .ConfigureAwait(false);
            await dataPackages
                .ReconcilePendingTransactionAdmittedAsync(recoveryLease, cancellationToken)
                .ConfigureAwait(false);
            return StartupStepResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            logStorage.AppendLog(
                "Error",
                "DataPackageRecovery",
                "data-package-recovery-required",
                exception.Message);
            return StartupStepResult.Fatal("data-package-recovery-required");
        }
    }
}

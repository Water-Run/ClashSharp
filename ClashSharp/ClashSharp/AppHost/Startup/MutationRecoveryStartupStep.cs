using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Resolves every retained mutation before any new startup side effect is admitted.</summary>
internal sealed class MutationRecoveryStartupStep(
    IMutationJournalStore journalStore,
    MutationAdmissionBarrier admissionBarrier,
    IApplicationMutationCoordinator mutations,
    LogStorageService logStorage) : IStartupStep
{
    public string Name => "mutation-recovery";

    public int Order => 150;

    public async Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        MutationJournalSnapshot? snapshot;
        try
        {
            snapshot = await journalStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logStorage.AppendLog(
                "Error",
                "MutationRecovery",
                "mutation-journal-load-failed",
                exception.Message);
            return StartupStepResult.Fatal("mutation-journal-load-failed");
        }
        if (snapshot is null)
        {
            return StartupStepResult.Succeeded();
        }

        admissionBarrier.EnterRecoveryOnly();
        MutationResult<object?> recovery = await mutations
            .RetryRecoveryAsync(snapshot.Journal.OperationId, cancellationToken)
            .ConfigureAwait(false);
        return recovery.Outcome is MutationOutcome.Succeeded or MutationOutcome.Compensated
            ? StartupStepResult.Succeeded()
            : StartupStepResult.Fatal(recovery.ErrorCode ?? "mutation-recovery-required");
    }
}

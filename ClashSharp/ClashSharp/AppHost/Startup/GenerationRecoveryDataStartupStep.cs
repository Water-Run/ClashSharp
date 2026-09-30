using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Hosting.Startup;

/// <summary>Uses managed data for recovery when it exists, while leaving first migration after legacy journal recovery.</summary>
internal sealed class GenerationRecoveryDataStartupStep(IDataGenerationStore store, DataGenerationStartupStep data) : IStartupStep
{
    public string Name => "recovery-data-generation";
    public int Order => 142;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        DataGenerationManifestSnapshot? current;
        try { current = await store.LoadCurrentAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)
            && !ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken))
        {
            return StartupStepResult.Fatal("data-generation.pointer_unavailable");
        }
        return await data.PrepareRecoveryDataAsync(current, cancellationToken).ConfigureAwait(false);
    }
}

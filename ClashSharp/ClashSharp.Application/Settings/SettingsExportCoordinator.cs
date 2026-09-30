using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Keeps backup reads within one drained settings and profile mutation boundary.</summary>
public sealed class SettingsExportCoordinator(MutationAdmissionBarrier admission)
{
    private readonly MutationAdmissionBarrier _admission = admission ?? throw new ArgumentNullException(nameof(admission));

    /// <summary>Waits for admitted changes and retains exclusive admission until the complete export finishes.</summary>
    /// <remarks>The export owns its atomic destination publication; cancellation never releases a running callback.</remarks>
    /// <param name="export">The complete snapshot and publication operation, without dialogs or further admission.</param>
    /// <param name="cancellationToken">Cancels waiting or the export before its publication boundary.</param>
    public async Task ExecuteAsync(Func<CancellationToken, Task> export, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);
        await ExecuteAsync((_, token) => export(token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Passes the exact exclusive owner to generation-backed backup operations.</summary>
    /// <param name="export">Complete export using the already-owned admission lease.</param>
    /// <param name="cancellationToken">Cancels waiting or work before destination publication.</param>
    public async Task ExecuteAsync(Func<MutationAdmissionLease, CancellationToken, Task> export, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);
        using MutationAdmissionLease lease = await _admission.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await export(lease, cancellationToken).ConfigureAwait(false);
    }
}

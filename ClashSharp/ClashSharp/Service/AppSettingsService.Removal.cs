using System;
using System.Threading;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Service;

public sealed partial class AppSettingsService
{
    /// <summary>Erases obsolete migration input only after terminal shutdown; this is not a settings write path.</summary>
    internal void ClearLegacySettingsForRemoval(MutationAdmissionLease lease)
    {
        MutationAdmissionBarrier admission = Volatile.Read(ref _mutationAdmission);
        admission.EnsureActiveExclusiveLease(lease);
        if (admission.State != MutationAdmissionState.ClosedForShutdown)
        {
            throw new InvalidOperationException("Legacy migration input can only be erased after terminal shutdown.");
        }
        lock (_syncLock)
        {
            admission.EnsureActiveExclusiveLease(lease);
            Exception? deletionFailure = null;
            try { _values.Clear(); }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { deletionFailure = failure; }
            if (_values.Count != 0)
            {
                throw new InvalidOperationException("Legacy settings removal was not verified.", deletionFailure);
            }
        }
    }
}

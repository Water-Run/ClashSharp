using System;
using System.Threading;

namespace ClashSharp.Service;

/// <summary>Identifies cooperative catalog cancellation caused by revoked mutation admission.</summary>
/// <remarks>
/// Only the admission coordinator creates this signal after observing its own revocation token.
/// A provider timeout can also carry a canceled token, so background callers must not infer this
/// condition from an arbitrary OperationCanceledException or its cancellation token alone.
/// </remarks>
internal sealed class ProfileCatalogMutationCanceledException : OperationCanceledException
{
    internal ProfileCatalogMutationCanceledException(
        OperationCanceledException innerException,
        CancellationToken cancellationToken)
        : base("The admitted profile catalog mutation was canceled.", innerException, cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        if (!cancellationToken.IsCancellationRequested)
        {
            throw new ArgumentException("Admission cancellation must already be requested.", nameof(cancellationToken));
        }
    }
}

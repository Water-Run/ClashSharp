using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Hosting;

/// <summary>Separates normal activation from one-shot recovery while serializing their host lifetimes.</summary>
internal static class StartupInstanceArbitration
{
    internal static async Task<PrimaryInstanceOwnership> AcquireAsync(
        bool isRestoreFallback,
        Func<bool, bool> tryRegister,
        Func<bool, int?> findOtherProcess,
        Func<int, CancellationToken, Task> waitForExitAsync,
        Func<CancellationToken, Task> redirectAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!tryRegister(isRestoreFallback))
        {
            // Recovery never activates a window or forwards an ordinary launch to a helper.
            if (!isRestoreFallback)
            {
                await redirectAsync(cancellationToken);
            }

            return PrimaryInstanceOwnership.Redirected;
        }

        if (isRestoreFallback)
        {
            // Registration precedes discovery in both roles: either the helper yields to
            // the normal instance or the normal instance waits for this helper to exit.
            return findOtherProcess(false) is null
                ? PrimaryInstanceOwnership.Primary
                : PrimaryInstanceOwnership.Redirected;
        }

        if (findOtherProcess(true) is int helperProcessId)
        {
            await waitForExitAsync(helperProcessId, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return PrimaryInstanceOwnership.Primary;
    }
}

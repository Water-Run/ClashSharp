namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Owns one foreground confirmation attempt without losing deferred installation intent.</summary>
/// <remarks>
/// The caller supplies its current window context and stays on its UI thread. Only one invocation
/// may inspect or consume a request at a time, including reentrant window activation callbacks.
/// </remarks>
public sealed class InstallationPinningCoordinator
{
    private int _running;

    /// <summary>Observes, consumes and offers a matching installation request at most once.</summary>
    /// <param name="operations">Owned foreground-window context and correlated request store.</param>
    /// <param name="cancellationToken">Window lifetime cancellation.</param>
    public async Task<InstallationPinningStatus> CompleteAsync(
        IInstallationPinningOperations operations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) { return InstallationPinningStatus.Deferred; }
        try
        {
            if (!operations.IsForeground) { return InstallationPinningStatus.Deferred; }
            string? request = operations.ReadPendingRequest();
            if (request is null) { return InstallationPinningStatus.NoRequest; }
            InstallationPinningStatus status = await operations.GetStatusAsync(cancellationToken);
            if (status is not (InstallationPinningStatus.Ready or InstallationPinningStatus.AlreadyPinned or InstallationPinningStatus.Unavailable))
            {
                throw new InvalidOperationException("Unexpected taskbar availability result.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!operations.IsForeground) { return InstallationPinningStatus.Deferred; }
            cancellationToken.ThrowIfCancellationRequested();
            if (!operations.TryConsumeRequest(request)) { return InstallationPinningStatus.NoRequest; }
            if (status != InstallationPinningStatus.Ready) { return status; }

            // No cancellable or foreground-dependent gap after consuming the request. Native
            // refusal or cancellation after submission is terminal, preventing repeated prompts.
            InstallationPinningStatus result = await operations.RequestPinAsync(cancellationToken);
            return result is InstallationPinningStatus.Pinned or InstallationPinningStatus.NotPinned or InstallationPinningStatus.Unavailable
                ? result : throw new InvalidOperationException("Unexpected taskbar confirmation result.");
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}

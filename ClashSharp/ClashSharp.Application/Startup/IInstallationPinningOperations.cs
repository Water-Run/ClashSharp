namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Current-window shell operations and correlated one-shot installation request storage.</summary>
public interface IInstallationPinningOperations
{
    /// <summary>Whether the owning application window is currently foreground.</summary>
    bool IsForeground { get; }

    /// <summary>Reads a canonical pending request without marking it handled.</summary>
    string? ReadPendingRequest();

    /// <summary>Marks only the still-matching request handled before any native confirmation.</summary>
    /// <param name="requestId">Previously observed request identity.</param>
    bool TryConsumeRequest(string requestId);

    /// <summary>Observes Ready, AlreadyPinned or Unavailable without displaying confirmation.</summary>
    /// <param name="cancellationToken">Cancellation before the request has been consumed.</param>
    Task<InstallationPinningStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Offers exactly one native confirmation after the request has been consumed.</summary>
    /// <param name="cancellationToken">Cancels observation of an already initiated native request.</param>
    /// <remarks>
    /// Initiate the native request synchronously before observing cancellation. The coordinator
    /// has committed this attempt; a cancelled await must not cause repeated user prompts.
    /// </remarks>
    Task<InstallationPinningStatus> RequestPinAsync(CancellationToken cancellationToken);
}

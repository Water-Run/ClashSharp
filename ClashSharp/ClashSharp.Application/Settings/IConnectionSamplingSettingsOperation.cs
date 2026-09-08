using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Connects sampling transactions to the settings authority and the owned loop.</summary>
public interface IConnectionSamplingSettingsOperation
{
    /// <summary>Reads both sampling preferences as one snapshot.</summary>
    ConnectionSamplingSettings ReadSettings();

    /// <summary>Writes both preferences in one validated batch under the caller's admission.</summary>
    /// <param name="settings">Complete preference pair.</param>
    /// <param name="admissionLease">Active lease retained until verification or recovery finishes.</param>
    void WriteSettings(ConnectionSamplingSettings settings, MutationAdmissionLease admissionLease);

    /// <summary>Gets whether an owned sampling loop is currently active.</summary>
    bool IsRunning { get; }

    /// <summary>Temporarily stops and drains the loop without ending its application lifetime.</summary>
    /// <param name="cancellationToken">Cancellation for the awaited lifecycle transition.</param>
    Task QuiesceAsync(CancellationToken cancellationToken);

    /// <summary>Starts the loop independently of the saved enable preference, including baseline recovery.</summary>
    /// <param name="cancellationToken">Cancellation for the awaited lifecycle transition.</param>
    Task StartAsync(CancellationToken cancellationToken);
}

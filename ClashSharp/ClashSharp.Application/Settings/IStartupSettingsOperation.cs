using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Connects startup preferences and verified platform registration without owning admission.</summary>
public interface IStartupSettingsOperation
{
    /// <summary>Reads the current authoritative preference without changing it.</summary>
    bool ReadPreference();

    /// <summary>Writes the preference through the supplied active settings admission lease.</summary>
    /// <param name="enabled">Preference to persist.</param>
    /// <param name="admissionLease">Caller-owned admission retained through publication.</param>
    void WritePreference(bool enabled, MutationAdmissionLease admissionLease);

    /// <summary>Reads actual platform registration, returning null when it cannot be established.</summary>
    /// <param name="cancellationToken">Cancels observation before mutation begins.</param>
    Task<bool?> ReadRegistrationAsync(CancellationToken cancellationToken);

    /// <summary>Applies the requested platform registration without writing the preference.</summary>
    /// <param name="enabled">Registration to apply.</param>
    /// <param name="cancellationToken">Operation token; admitted mutation and compensation are awaited without cancellation.</param>
    Task ApplyRegistrationAsync(bool enabled, CancellationToken cancellationToken);
}

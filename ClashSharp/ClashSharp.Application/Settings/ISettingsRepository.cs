using ClashSharp.ApplicationModel.Data;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Persists one immutable settings envelope inside one pinned data generation.</summary>
public interface ISettingsRepository
{
    /// <summary>Gets the immutable generation descriptor that owns every repository path.</summary>
    DataGenerationDescriptor Generation { get; }

    /// <summary>Opens storage, cleans candidates, and recovers a valid backup when required.</summary>
    Task<SettingsPersistenceResult> OpenAsync(CancellationToken cancellationToken);

    /// <summary>Atomically replaces the envelope when the expected revision is still current.</summary>
    Task<SettingsPersistenceResult> SaveAsync(
        SettingsEnvelope envelope,
        long expectedRevision,
        CancellationToken cancellationToken);
}

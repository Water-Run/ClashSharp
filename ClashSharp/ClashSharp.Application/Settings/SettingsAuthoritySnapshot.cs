using ClashSharp.ApplicationModel.Data;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Captures one immutable authority projection with its exact generation identity.</summary>
public sealed class SettingsAuthoritySnapshot
{
    /// <summary>Creates a projection from one verified generation and envelope.</summary>
    /// <param name="generation">Immutable storage owner of the captured envelope.</param>
    /// <param name="envelope">Complete immutable desired, applied, and pending state.</param>
    public SettingsAuthoritySnapshot(DataGenerationDescriptor generation, SettingsEnvelope envelope)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
    }

    /// <summary>Gets the captured generation identity.</summary>
    public DataGenerationDescriptor Generation { get; }

    /// <summary>Gets the complete immutable settings state.</summary>
    public SettingsEnvelope Envelope { get; }
}

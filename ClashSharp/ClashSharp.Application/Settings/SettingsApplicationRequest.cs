using System.Collections.ObjectModel;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Specifies whether application occurs during an exclusively owned startup or a live command.</summary>
public enum SettingsApplicationPhase
{
    /// <summary>Live application cannot consume restart-bound batches.</summary>
    Live,
    /// <summary>Startup application requires an active exclusive mutation lease.</summary>
    Startup,
}

/// <summary>Captures the exact generation, attempt, and immutable desired values assigned to a participant.</summary>
public sealed class SettingsApplicationRequest
{
    internal SettingsApplicationRequest(
        DataGenerationDescriptor generation, SettingsEnvelope envelope, SettingsApplicationBatch batch,
        SettingsApplicationPhase phase)
    {
        Generation = generation;
        Envelope = envelope;
        Batch = batch;
        Phase = phase;
        Values = new ReadOnlyDictionary<SettingKey, SettingValue>(
            batch.Entries.ToDictionary(entry => entry.Key, entry => envelope.Desired[entry.Key].Value));
    }

    /// <summary>Gets the pinned storage generation.</summary>
    public DataGenerationDescriptor Generation { get; }

    /// <summary>Gets the durable running envelope, including companion desired settings.</summary>
    public SettingsEnvelope Envelope { get; }

    /// <summary>Gets the exact current attempt.</summary>
    public SettingsApplicationBatch Batch { get; }

    /// <summary>Gets the owned application phase.</summary>
    public SettingsApplicationPhase Phase { get; }

    /// <summary>Gets only this batch's canonical desired values.</summary>
    public IReadOnlyDictionary<SettingKey, SettingValue> Values { get; }
}

/// <summary>Contains independently probed values bound to one exact generation and application attempt.</summary>
public sealed class SettingsApplicationObservation
{
    /// <summary>Copies a completed participant probe without deriving observed values from the request.</summary>
    /// <param name="generation">Generation under which the participant was observed.</param>
    /// <param name="batchId">Observed batch identity.</param>
    /// <param name="attemptId">Observed attempt identity.</param>
    /// <param name="values">Independent effective values; the authority validates complete canonical coverage.</param>
    public SettingsApplicationObservation(
        DataGenerationDescriptor generation, Guid batchId, Guid attemptId, IEnumerable<SettingValueChange> values)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        ArgumentNullException.ThrowIfNull(values);
        BatchId = batchId;
        AttemptId = attemptId;
        Values = Array.AsReadOnly(values.ToArray());
    }

    /// <summary>Gets the generation actually observed.</summary>
    public DataGenerationDescriptor Generation { get; }

    /// <summary>Gets the batch identity of the observation.</summary>
    public Guid BatchId { get; }

    /// <summary>Gets the current attempt identity of the observation.</summary>
    public Guid AttemptId { get; }

    /// <summary>Gets the defensively copied effective values.</summary>
    public IReadOnlyList<SettingValueChange> Values { get; }
}

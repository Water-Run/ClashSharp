using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>Seeded generators over the real settings registry so new settings are covered without test edits.</summary>
internal static class SettingsGenerators
{
    public const int Seed = 20261109;

    public static Guid NextGuid(Random random)
    {
        byte[] bytes = new byte[16];
        random.NextBytes(bytes);
        bytes[0] |= 1;
        return new Guid(bytes);
    }

    /// <summary>Returns canonical alternatives a definition accepts, always including its default value.</summary>
    public static IReadOnlyList<SettingValue> Candidates(SettingDefinition definition)
    {
        List<SettingValue> candidates = [definition.DefaultValue];
        if (!candidates.Contains(definition.SafeFallback))
        {
            candidates.Add(definition.SafeFallback);
        }

        foreach (SettingValue allowed in definition.AllowedValues)
        {
            if (!candidates.Contains(allowed))
            {
                candidates.Add(allowed);
            }
        }

        if (definition.IntegerRange is SettingIntegerRange range)
        {
            foreach (int probe in new[] { range.Minimum, range.Maximum, range.Minimum + ((range.Maximum - range.Minimum) / 2) })
            {
                SettingNormalizationResult result = definition.Normalize(
                    probe.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (result.IsSuccess && !candidates.Contains(result.Value!))
                {
                    candidates.Add(result.Value!);
                }
            }
        }

        return candidates;
    }

    public static SettingValue Pick(Random random, SettingDefinition definition)
    {
        IReadOnlyList<SettingValue> candidates = Candidates(definition);
        return candidates[random.Next(candidates.Count)];
    }

    /// <summary>Definitions for which at least two distinct canonical values exist.</summary>
    public static IReadOnlyList<SettingDefinition> MutableDefinitions(SettingsRegistry registry) =>
        [.. registry.Definitions.Where(definition => Candidates(definition).Count > 1)];

    public static string Hash(SettingValue value) => SettingsApplicationBatchEntry.ComputeValueHash(value);

    /// <summary>
    /// Produces a valid envelope by driving the real editors with seeded random commands, so every shape the
    /// product can legitimately persist (pending, running, failed, unknown evidence, migration history) occurs.
    /// </summary>
    public static SettingsEnvelope RandomEnvelope(SettingsRegistry registry, Random random, int steps)
    {
        SettingsEnvelopeEditor editor = new(registry);
        SettingsApplicationBatchEditor batches = new(registry);
        IReadOnlyList<SettingDefinition> mutable = MutableDefinitions(registry);
        SettingsEnvelope envelope = SettingsEnvelopeTestData.CreateMatchingEnvelope(registry);
        DateTimeOffset observed = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        for (int step = 0; step < steps; step++)
        {
            SettingsEnvelopeEditResult result;
            if (mutable.Count > 0 && (envelope.PendingApplications.Count == 0 || random.Next(3) == 0))
            {
                SettingDefinition definition = mutable[random.Next(mutable.Count)];
                result = editor.ApplyChanges(
                    envelope,
                    [new SettingValueChange(definition.Key, Pick(random, definition))],
                    NextGuid(random));
            }
            else if (envelope.PendingApplications.Count > 0)
            {
                SettingsApplicationBatch batch = envelope.PendingApplications[random.Next(envelope.PendingApplications.Count)];
                result = batch.State switch
                {
                    SettingsApplicationBatchState.Pending => batches.BeginAttempt(envelope, batch.BatchId, batch.AttemptId),
                    SettingsApplicationBatchState.Running when random.Next(3) == 0 => batches.FailAttempt(
                        envelope, batch.BatchId, batch.AttemptId, new SettingsApplicationError("generated.failure")),
                    SettingsApplicationBatchState.Running => batches.CompleteAttempt(
                        envelope,
                        batch.BatchId,
                        batch.AttemptId,
                        batch.Entries.Select(entry => new SettingValueChange(entry.Key, envelope.Desired[entry.Key].Value)),
                        SettingAppliedValueSource.RuntimeProbe,
                        observed.AddMinutes(step)),
                    _ => batches.RetryFailed(envelope, batch.BatchId, batch.AttemptId, NextGuid(random)),
                };
            }
            else
            {
                break;
            }

            if (result.IsSuccess)
            {
                envelope = result.Envelope;
            }
        }

        return envelope;
    }
}

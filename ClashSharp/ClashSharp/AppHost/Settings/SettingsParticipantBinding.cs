using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Settings;

/// <summary>Rejects foreign generations and admissions before accessing a generation-owned runtime component.</summary>
internal sealed class SettingsParticipantBinding
{
    private readonly DataGenerationDescriptor _generation;
    private readonly MutationAdmissionBarrier _admission;
    private readonly SettingApplicationKind _kind;
    private readonly HashSet<SettingKey> _keys;

    public SettingsParticipantBinding(DataGenerationDescriptor generation, MutationAdmissionBarrier admission,
        SettingApplicationKind kind, params SettingKey[] keys)
    {
        _generation = generation ?? throw new ArgumentNullException(nameof(generation));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _kind = kind;
        _keys = keys.ToHashSet();
    }

    public void Validate(SettingsApplicationRequest request, MutationAdmissionLease lease)
    {
        ArgumentNullException.ThrowIfNull(request);
        _admission.EnsureActiveLease(lease);
        if (!_generation.IsSameGeneration(request.Generation) || request.Batch.ApplicationKind != _kind
            || request.Values.Count == 0 || request.Values.Keys.Any(key => !_keys.Contains(key)))
        {
            throw new InvalidOperationException("The settings attempt does not belong to this runtime participant.");
        }
    }

    public SettingsApplicationObservation Observe(SettingsApplicationRequest request, Func<SettingKey, object> read) =>
        new(_generation, request.Batch.BatchId, request.Batch.AttemptId,
            request.Values.Keys.Select(key => new SettingValueChange(key,
                SettingsRegistry.Default.Get(key.Value).NormalizeValue(read(key)).Value
                ?? throw new InvalidOperationException("The runtime returned a noncanonical settings value."))));
}

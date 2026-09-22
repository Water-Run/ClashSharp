using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Owns atomic installation and independent observation of the application-internal consumer configuration.</summary>
/// <remarks>Defaults initialize the actual consumer contract without claiming durable application. The containing generation owns retirement.</remarks>
public sealed class InternalSettingsParticipant : ISettingsApplicationParticipant, IInternalSettingsReader, IDisposable
{
    private readonly object _gate = new();
    private readonly DataGenerationDescriptor _generation;
    private readonly MutationAdmissionBarrier _admission;
    private readonly IReadOnlyDictionary<SettingKey, SettingDefinition> _definitions;
    private InternalSettingsSnapshot? _installed;

    /// <summary>Creates a pure memory owner without opening storage, reading legacy preferences or starting tasks.</summary>
    /// <param name="generation">Immutable consumer lifetime.</param>
    /// <param name="admission">The process-wide admission owner used by the settings authority.</param>
    /// <param name="registry">Canonical definitions used by the same generation's settings session.</param>
    public InternalSettingsParticipant(DataGenerationDescriptor generation, MutationAdmissionBarrier admission, SettingsRegistry registry)
    {
        _generation = generation ?? throw new ArgumentNullException(nameof(generation));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        ArgumentNullException.ThrowIfNull(registry);
        _definitions = registry.Definitions.Where(definition => definition.ApplicationKind == SettingApplicationKind.Internal)
            .ToDictionary(definition => definition.Key);
        if (_definitions.Count == 0 || _definitions.Values.Any(definition => definition.Authority != SettingAuthority.Internal))
        {
            throw new ArgumentException("Internal settings require application-owned definitions.", nameof(registry));
        }
        _installed = new(_generation, _definitions.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.DefaultValue)));
    }

    /// <inheritdoc />
    public SettingApplicationKind ApplicationKind => SettingApplicationKind.Internal;

    /// <summary>Reads one immutable installed snapshot; subsequent changes cannot modify an already captured value.</summary>
    /// <returns>The complete current consumer configuration from this generation.</returns>
    public InternalSettingsSnapshot CaptureSnapshot() => Volatile.Read(ref _installed)
        ?? throw new ObjectDisposedException(nameof(InternalSettingsParticipant));

    /// <inheritdoc />
    public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Validate(request, admissionLease);
            cancellationToken.ThrowIfCancellationRequested();
            InternalSettingsSnapshot snapshot = CaptureSnapshot();
            return Task.FromResult(new SettingsApplicationObservation(_generation, request.Batch.BatchId, request.Batch.AttemptId,
                request.Values.Keys.Select(key => new SettingValueChange(key, snapshot.Values[key]))));
        }
    }

    /// <inheritdoc />
    public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Validate(request, admissionLease);
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<SettingKey, SettingValue> installed = CaptureSnapshot().Values.ToDictionary();
            foreach ((SettingKey key, SettingValue value) in request.Values) { installed[key] = value; }
            InternalSettingsSnapshot next = new(_generation, installed);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _installed, next);
            return Task.CompletedTask;
        }
    }

    /// <summary>Rejects future calls without changing historical snapshots or durable preferences.</summary>
    public void Dispose()
    {
        lock (_gate) { Volatile.Write(ref _installed, null); }
    }

    private void Validate(SettingsApplicationRequest request, MutationAdmissionLease lease)
    {
        ArgumentNullException.ThrowIfNull(request);
        _admission.EnsureActiveLease(lease);
        _ = CaptureSnapshot();
        if (request.Phase == SettingsApplicationPhase.Startup) { _admission.EnsureActiveExclusiveLease(lease); }
        if (!_generation.IsSameGeneration(request.Generation) || request.Batch.ApplicationKind != ApplicationKind || request.Values.Count == 0)
        {
            throw new InvalidOperationException("The internal settings attempt belongs to another participant or generation.");
        }
        foreach ((SettingKey key, SettingValue value) in request.Values)
        {
            if (!_definitions.TryGetValue(key, out SettingDefinition? definition)
                || definition.ApplicationTiming == SettingApplicationTiming.Restart && request.Phase != SettingsApplicationPhase.Startup
                || !value.Equals(definition.Normalize(value.CanonicalText).Value))
            {
                throw new InvalidOperationException("The internal settings attempt contains an unsupported key or value.");
            }
        }
    }
}

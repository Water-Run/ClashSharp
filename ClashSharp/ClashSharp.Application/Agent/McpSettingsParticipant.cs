using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Applies local agent settings through an observed generation-owned runtime and participates in lifecycle drain.</summary>
public sealed class McpSettingsParticipant : ISettingsApplicationParticipant, IRuntimeParticipant, IAsyncDisposable
{
    private readonly DataGenerationDescriptor _generation;
    private readonly MutationAdmissionBarrier _admission;
    private readonly SettingsRegistry _registry;
    private readonly McpRuntimeController _runtime;

    /// <summary>Creates the participant without reading credentials or starting the endpoint.</summary>
    /// <param name="generation">Immutable data owner.</param>
    /// <param name="admission">Shared product-operation barrier.</param>
    /// <param name="registry">Canonical local agent definitions.</param>
    /// <param name="runtime">Owned runtime, credential store and transport.</param>
    public McpSettingsParticipant(DataGenerationDescriptor generation, MutationAdmissionBarrier admission,
        SettingsRegistry registry, McpRuntimeController runtime)
    {
        _generation = generation ?? throw new ArgumentNullException(nameof(generation));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <inheritdoc />
    public SettingApplicationKind ApplicationKind => SettingApplicationKind.McpServer;
    /// <inheritdoc />
    public string Name => "mcp";
    /// <summary>Gets the owned controller for explicit user management commands.</summary>
    public McpRuntimeController Runtime => _runtime;

    /// <inheritdoc />
    public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        Validate(request, admissionLease);
        cancellationToken.ThrowIfCancellationRequested();
        McpServerConfiguration actual = _runtime.CaptureConfiguration();
        return Task.FromResult(new SettingsApplicationObservation(_generation, request.Batch.BatchId, request.Batch.AttemptId,
            request.Values.Keys.Select(key => new SettingValueChange(key, Read(key, actual)))));
    }

    /// <inheritdoc />
    public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        Validate(request, admissionLease);
        _admission.EnsureActiveExclusiveLease(admissionLease);
        McpServerConfiguration before = _runtime.CaptureSnapshot().Configuration;
        T Target<T>(SettingKey key, T fallback) where T : notnull => request.Values.TryGetValue(key, out SettingValue? value) ? value.Get<T>() : fallback;
        McpServerConfiguration target = new(Target(SettingsRegistry.Keys.McpServerPort, before.Port),
            new(Target(SettingsRegistry.Keys.McpServerEnabled, before.Access.Enabled),
                (McpAccessScope)Target(SettingsRegistry.Keys.McpAllowedScopes, (int)before.Access.Scopes)));
        return _runtime.ApplyAdmittedAsync(target, admissionLease, cancellationToken);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _runtime.ResumeAsync(cancellationToken);
    /// <inheritdoc />
    public async Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
    {
        bool wasRunning = _runtime.CaptureSnapshot().BoundEndpoint is not null;
        await _runtime.SuspendAsync(cancellationToken).ConfigureAwait(false);
        return new(wasRunning);
    }
    /// <inheritdoc />
    public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken) => priorState.WasRunning
        ? _runtime.ResumeAsync(cancellationToken) : Task.CompletedTask;
    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _runtime.SuspendAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask DisposeAsync() => _runtime.DisposeAsync();

    private SettingValue Read(SettingKey key, McpServerConfiguration actual)
    {
        object value = key == SettingsRegistry.Keys.McpServerEnabled ? actual.Access.Enabled
            : key == SettingsRegistry.Keys.McpServerPort ? actual.Port : (int)actual.Access.Scopes;
        SettingNormalizationResult normalized = _registry.Get(key.Value).NormalizeValue(value);
        return normalized.IsSuccess ? normalized.Value! : throw new InvalidOperationException("The MCP runtime returned a noncanonical value.");
    }

    private void Validate(SettingsApplicationRequest request, MutationAdmissionLease lease)
    {
        ArgumentNullException.ThrowIfNull(request);
        _admission.EnsureActiveLease(lease);
        if (!_generation.IsSameGeneration(request.Generation) || request.Batch.ApplicationKind != ApplicationKind
            || request.Values.Count == 0 || request.Values.Any(pair => _registry.Get(pair.Key.Value).ApplicationKind != ApplicationKind
                || !pair.Value.Equals(_registry.Get(pair.Key.Value).Normalize(pair.Value.CanonicalText).Value)))
        {
            throw new InvalidOperationException("The MCP application attempt does not match its generation or canonical settings.");
        }
    }
}

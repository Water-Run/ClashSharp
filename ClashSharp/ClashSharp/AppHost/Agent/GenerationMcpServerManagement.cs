using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Hosting.Data;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Agent;

/// <summary>Retains management commands through exclusive admission and the complete generation-owned effect.</summary>
internal sealed class GenerationMcpServerManagement(DataGenerationManager generations, MutationAdmissionBarrier admission,
    IRuntimeSettingsAuthority authority) : IMcpServerManagement
{
    public async Task<McpSettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(cancellationToken).ConfigureAwait(false);
        return await generations.ExecuteAsync<AppDataGenerationRuntime, McpSettingsSnapshot>(
            (runtime, _, token) => Task.FromResult(Capture(runtime)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpSettingsSnapshot> ApplyAsync(McpServerConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false);
        return await generations.ExecuteAsync<AppDataGenerationRuntime, McpSettingsSnapshot>(async (runtime, _, token) =>
        {
            SettingsAuthorityResult result = await authority.ApplyChangesAdmittedAsync([
                Change(SettingsRegistry.Keys.McpServerEnabled, configuration.Access.Enabled),
                Change(SettingsRegistry.Keys.McpServerPort, configuration.Port),
                Change(SettingsRegistry.Keys.McpAllowedScopes, (int)configuration.Access.Scopes)], Guid.NewGuid(), lease, token).ConfigureAwait(false);
            if (!result.IsSucceeded) { throw new InvalidOperationException("MCP settings were not completely applied."); }
            return Capture(runtime);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetClientConfigurationAsync(CancellationToken cancellationToken)
    {
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false);
        return await generations.ExecuteAsync<McpSettingsParticipant, string>((participant, _, token) =>
            participant.Runtime.GetClientConfigurationAdmittedAsync(lease, token), cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpSettingsSnapshot> RotateCredentialAsync(CancellationToken cancellationToken)
    {
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false);
        return await generations.ExecuteAsync<AppDataGenerationRuntime, McpSettingsSnapshot>(async (runtime, _, token) =>
        {
            try { await runtime.Mcp.Runtime.RotateCredentialAdmittedAsync(lease, token).ConfigureAwait(false); }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
            {
                // Keep failure closed across process restarts as well as in the current credential verifier.
                SettingsAuthorityResult disabled = await authority.ApplyChangesAdmittedAsync(
                    [Change(SettingsRegistry.Keys.McpServerEnabled, false)], Guid.NewGuid(), lease, CancellationToken.None).ConfigureAwait(false);
                if (!disabled.IsSucceeded) { throw new InvalidOperationException("MCP credential recovery requires settings repair."); }
                throw new InvalidOperationException("MCP credential replacement failed; the endpoint was disabled.");
            }
            return Capture(runtime);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static McpSettingsSnapshot Capture(AppDataGenerationRuntime runtime)
    {
        SettingsEnvelope settings = runtime.Repositories.Session.Snapshot;
        return new(new(settings.Desired[SettingsRegistry.Keys.McpServerPort].Value.Get<int>(),
            new(settings.Desired[SettingsRegistry.Keys.McpServerEnabled].Value.Get<bool>(),
                (McpAccessScope)settings.Desired[SettingsRegistry.Keys.McpAllowedScopes].Value.Get<int>())), runtime.Mcp.Runtime.CaptureSnapshot());
    }

    private static SettingValueChange Change(SettingKey key, object value) => new(key, SettingsRegistry.Default.Get(key.Value).NormalizeValue(value).Value!);
}

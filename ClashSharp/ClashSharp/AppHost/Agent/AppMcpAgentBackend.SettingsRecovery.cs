using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Agent;

internal sealed partial class AppMcpAgentBackend
{
    private async Task<(bool Exclusive, McpToolResponse? Response)> PrepareRetryAdmissionAsync(
        McpClientIdentity identity, JsonElement arguments, CancellationToken cancellationToken)
    {
        // Inspect only under ordinary admission and the exact repository generation. Release
        // both before requesting exclusive admission; upgrading a retained lease would deadlock.
        // The write path checks identity, current grants and immutable attempt IDs again.
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RevocationToken);
        try
        {
            await using DataGenerationLease pin = await generations.AcquireAsync(lifetime.Token).ConfigureAwait(false);
            if (!pin.Descriptor.IsSameGeneration(repositories.Generation)) { return (false, McpToolResponse.Error("mcp.generation_changed")); }
            if (!access.IsCurrentIdentity(identity) || !access.Policy.Allows(McpAccessScope.ModifyConfiguration))
            { return (false, McpToolResponse.Error("mcp.access_denied")); }
            await _writes.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                // A completed retry no longer has a failed batch. Resolve its exact receipt
                // before looking for pending work, without accessing the write cache unlocked.
                Guid operation = Guid.Parse(arguments.GetProperty("operation_id").GetString()!);
                if (_receipts.TryGetValue(operation, out var receipt))
                {
                    return (false, StringComparer.Ordinal.Equals(receipt.Hash, GetWriteHash(McpToolId.RetrySettingsApplication, arguments))
                        ? receipt.Response : McpToolResponse.Error("mcp.operation_id_conflict"));
                }
            }
            finally { _writes.Release(); }
            Guid batchId = Guid.Parse(arguments.GetProperty("batch_id").GetString()!);
            Guid attemptId = Guid.Parse(arguments.GetProperty("attempt_id").GetString()!);
            SettingsApplicationBatch? failed = repositories.Session.Snapshot.PendingApplications.SingleOrDefault(batch =>
                batch.BatchId == batchId && batch.AttemptId == attemptId && batch.State == SettingsApplicationBatchState.Failed);
            if (failed is null) { return (false, McpToolResponse.Error("mcp.settings_application_unavailable")); }
            if (!CanRetrySettingsBatch(failed, access.Policy)) { return (false, McpToolResponse.Error("mcp.setting_access_denied")); }
            return (failed.Entries.Any(entry => entry.Key == SettingsRegistry.Keys.TriggerNotificationsEnabled), null);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return (false, McpToolResponse.Error("mcp.cancelled"));
        }
    }

    private static bool CanRetrySettingsBatch(SettingsApplicationBatch batch, McpAccessPolicy policy)
    {
        // Classify every companion before checking one policy snapshot. No combination of
        // individually observed grants can authorize a batch that the user never granted whole.
        McpAccessScope required = McpAccessScope.ModifyConfiguration;
        foreach (SettingsApplicationBatchEntry entry in batch.Entries)
        {
            if (McpAccessRules.RequiredSettingWriteScopes(entry.Key) is not { } scopes) { return false; }
            required |= scopes;
        }
        return policy.Allows(required);
    }
}

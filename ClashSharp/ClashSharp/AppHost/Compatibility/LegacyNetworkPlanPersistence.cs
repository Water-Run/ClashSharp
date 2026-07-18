using System;
using System.Linq;
using System.Text.Json;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Serializes the exact legacy rollback material retained by the durable mutation journal.</summary>
internal static class LegacyNetworkPlanPersistence
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(
        NetworkIntent intent,
        NetworkStateSnapshot baseline,
        NetworkStateSnapshot desired,
        string baselineHash,
        string desiredHash,
        string baselineProxyServer,
        string desiredProxyServer,
        ClashSharpMode durableBaselineMode)
    {
        PersistedNetworkPlan persisted = new(
            SchemaVersion,
            intent,
            baseline,
            desired,
            baselineHash,
            desiredHash,
            baselineProxyServer,
            desiredProxyServer,
            durableBaselineMode);
        return JsonSerializer.Serialize(persisted, SerializerOptions);
    }

    public static PersistedNetworkPlan Deserialize(string compensationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compensationData);
        PersistedNetworkPlan persisted = JsonSerializer.Deserialize<PersistedNetworkPlan>(
            compensationData,
            SerializerOptions)
            ?? throw new InvalidOperationException("The network recovery payload is empty.");
        if (persisted.SchemaVersion != SchemaVersion
            || persisted.Intent is null
            || persisted.Baseline is null
            || persisted.Desired is null
            || string.IsNullOrWhiteSpace(persisted.BaselineHash)
            || string.IsNullOrWhiteSpace(persisted.DesiredHash))
        {
            throw new InvalidOperationException("The network recovery payload is unsupported or incomplete.");
        }

        return persisted;
    }

    public static PersistedNetworkPlan Restore(MutationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        MutationJournalStep step = journal.Steps.SingleOrDefault(
            static candidate => string.Equals(candidate.Name, "network-state", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The retained network journal has no network-state step.");
        PersistedNetworkPlan persisted = Deserialize(
            step.CompensationData
            ?? throw new InvalidOperationException("The retained network journal has no compensation payload."));
        if (!string.Equals(persisted.BaselineHash, journal.BaselineHash, StringComparison.Ordinal)
            || !string.Equals(persisted.DesiredHash, journal.DesiredHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The retained network plan does not match its journal identity.");
        }

        return persisted;
    }

    internal sealed record PersistedNetworkPlan(
        int SchemaVersion,
        NetworkIntent Intent,
        NetworkStateSnapshot Baseline,
        NetworkStateSnapshot Desired,
        string BaselineHash,
        string DesiredHash,
        string BaselineProxyServer,
        string DesiredProxyServer,
        ClashSharpMode DurableBaselineMode)
    {
        public NetworkPlan ToPlan(string compensationData)
        {
            return new NetworkPlan(
                Intent,
                Baseline,
                Desired,
                BaselineHash,
                DesiredHash,
                compensationData);
        }
    }
}

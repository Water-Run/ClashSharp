using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides stable, host-bound data ports to native factories without capturing directories or repositories.</summary>
/// <remarks>Access before binding or after host disposal fails; no retired host or generation becomes a fallback.</remarks>
internal static class RuntimeDataServices
{
    private static Binding? _owner;
    public static ICoreConfigurationStore Configuration { get; } = new ConfigurationPort();
    public static ILogStorage Logs { get; } = new LogPort();
    public static IProxySelectionService ProxySelections { get; } = new SelectionPort();

    internal static IDisposable Bind(ICoreConfigurationStore configuration, ILogStorage logs,
        IProxySelectionService selections, Func<CancellationToken, Task> flushSampling)
    {
        Binding binding = new(configuration, logs, selections, flushSampling);
        if (Interlocked.CompareExchange(ref _owner, binding, null) is not null)
        {
            throw new InvalidOperationException("Runtime data already belongs to another active host.");
        }
        return binding;
    }

    private static Binding RequireOwner() => Volatile.Read(ref _owner)
        ?? throw new InvalidOperationException("Runtime data access is unavailable before host ownership or after shutdown.");

    internal static Task FlushSamplingAsync(CancellationToken cancellationToken) => RequireOwner().FlushSampling(cancellationToken);

    private sealed class Binding(
        ICoreConfigurationStore configuration, ILogStorage logs, IProxySelectionService selections,
        Func<CancellationToken, Task> flushSampling) : IDisposable
    {
        public ICoreConfigurationStore Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));
        public ILogStorage Logs { get; } = logs ?? throw new ArgumentNullException(nameof(logs));
        public IProxySelectionService Selections { get; } = selections ?? throw new ArgumentNullException(nameof(selections));
        public Func<CancellationToken, Task> FlushSampling { get; } = flushSampling ?? throw new ArgumentNullException(nameof(flushSampling));
        public void Dispose() => Interlocked.CompareExchange(ref _owner, null, this);
    }

    private sealed class ConfigurationPort : ICoreConfigurationStore
    {
        public CoreConfigurationState GetState() => RequireOwner().Configuration.GetState();

        public CoreConfigurationState EnsureDefaultConfiguration() => RequireOwner().Configuration.EnsureDefaultConfiguration();

        public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode) => RequireOwner().Configuration.EnsureConfiguration(mode);

        public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled) => RequireOwner().Configuration.EnsureConfiguration(mode, transparentProxyEnabled);

        public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort) => RequireOwner().Configuration.EnsureConfiguration(mode, transparentProxyEnabled, mixedPort);

        public Task<ProfileImportResult> ImportProfileConfigurationAsync(string profileId, string profileName, string configurationText, CancellationToken cancellationToken) => RequireOwner().Configuration.ImportProfileConfigurationAsync(profileId, profileName, configurationText, cancellationToken);

        public string GetProfileConfigurationPath(string profileId) => RequireOwner().Configuration.GetProfileConfigurationPath(profileId);

        public bool TryReadProfileConfigurationText(string profileId, out string? configurationText) => RequireOwner().Configuration.TryReadProfileConfigurationText(profileId, out configurationText);

        public Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken) => RequireOwner().Configuration.ValidateImportedProfileAsync(profileId, cancellationToken);

        public Task<RuntimeConfigurationGenerationState> GetRuntimeGenerationStateAsync(CancellationToken cancellationToken) => RequireOwner().Configuration.GetRuntimeGenerationStateAsync(cancellationToken);

        public RuntimeConfigurationIntegrityObservation ObserveRuntimeConfigurationIntegrity() => RequireOwner().Configuration.ObserveRuntimeConfigurationIntegrity();

        public bool CanRecoverInterruptedRuntimeConfiguration(RuntimeConfigurationActivationPlan baselinePlan, RuntimeConfigurationActivationPlan desiredPlan) => RequireOwner().Configuration.CanRecoverInterruptedRuntimeConfiguration(baselinePlan, desiredPlan);

        public Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) => RequireOwner().Configuration.ApplyRuntimeConfigurationAsync(mode, transparentProxyEnabled, mixedPort, runtime, cancellationToken);

        public Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(string profileId, ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) => RequireOwner().Configuration.ApplyRuntimeConfigurationAsync(profileId, mode, transparentProxyEnabled, mixedPort, runtime, cancellationToken);

        public Task<string?> ReadImportedProfileConfigurationAsync(string profileId, CancellationToken cancellationToken) => RequireOwner().Configuration.ReadImportedProfileConfigurationAsync(profileId, cancellationToken);

        public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) => RequireOwner().Configuration.DeleteImportedProfileAsync(profileId, cancellationToken);

        public Task<ProfileRuntimeConfigurationTransactionResult> ImportAndApplyProfileConfigurationAsync(string profileId, string profileName, string configurationText, ClashSharpMode mode, bool effectiveTunEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) => RequireOwner().Configuration.ImportAndApplyProfileConfigurationAsync(profileId, profileName, configurationText, mode, effectiveTunEnabled, mixedPort, runtime, cancellationToken);
    }

    private sealed class LogPort : ILogStorage
    {
        public LogStorageSummary GetStorageSummary() => RequireOwner().Logs.GetStorageSummary();

        public TrafficStatisticsSummary GetTrafficStatisticsSummary() => RequireOwner().Logs.GetTrafficStatisticsSummary();

        public long GetTrafficBytesSince(DateTimeOffset cutoff) => RequireOwner().Logs.GetTrafficBytesSince(cutoff);

        public IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit) => RequireOwner().Logs.GetProfileTrafficRows(limit);

        public IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit) => RequireOwner().Logs.GetDailyTrafficRows(limit);

        public IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit) => RequireOwner().Logs.GetNodeTrafficRows(limit);

        public void UpsertNodeHealth(string nodeName, string regionCode, int? latencyMilliseconds) => RequireOwner().Logs.UpsertNodeHealth(nodeName, regionCode, latencyMilliseconds);

        public int? GetNodeLatencyMilliseconds(string nodeName) => RequireOwner().Logs.GetNodeLatencyMilliseconds(nodeName);

        public void EnsureRuleHitRows(IEnumerable<RulePreview> rules) => RequireOwner().Logs.EnsureRuleHitRows(rules);

        public void IncrementRuleHit(string ruleName, long increment = 1) => RequireOwner().Logs.IncrementRuleHit(ruleName, increment);

        public int AppendConnectionSnapshot(IEnumerable<ActiveConnection> connections) => RequireOwner().Logs.AppendConnectionSnapshot(connections);

        public int AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot) => RequireOwner().Logs.AppendTrafficSnapshot(snapshot);

        public IReadOnlyDictionary<string, long> GetRuleHitCounts() => RequireOwner().Logs.GetRuleHitCounts();

        public void AppendLog(string level, string source, string message, string? detail) => RequireOwner().Logs.AppendLog(level, source, message, detail);

        public IReadOnlyList<LogRecord> GetRecentLogs(int limit) => RequireOwner().Logs.GetRecentLogs(limit);

        public IReadOnlyList<LogRecord> GetRecentLogs(string source, int limit) => RequireOwner().Logs.GetRecentLogs(source, limit);

        public IReadOnlyList<LogRecord> GetLogs(int limit, string? source = null, string? level = null, string? searchText = null) => RequireOwner().Logs.GetLogs(limit, source, level, searchText);

        public LogCleanupPreview PreviewLogCleanup(string? level = null, string? source = null) => RequireOwner().Logs.PreviewLogCleanup(level, source);

        public long CleanupLogs(string? level = null, string? source = null) => RequireOwner().Logs.CleanupLogs(level, source);

        public IReadOnlyList<string> GetLogSources() => RequireOwner().Logs.GetLogSources();

        public void ExportDatabase(string destinationPath) => RequireOwner().Logs.ExportDatabase(destinationPath);

        public void CleanupBefore(DateTimeOffset cutoff) => RequireOwner().Logs.CleanupBefore(cutoff);

        public void CleanupToSize(long targetSizeBytes) => RequireOwner().Logs.CleanupToSize(targetSizeBytes);

        public void CleanupToLogCount(long maxLogCount) => RequireOwner().Logs.CleanupToLogCount(maxLogCount);

        public void ClearAll() => RequireOwner().Logs.ClearAll();

        public long PreviewCleanupBefore(DateTimeOffset cutoff) => RequireOwner().Logs.PreviewCleanupBefore(cutoff);

        public long PreviewCleanupToLogCount(long maxLogCount) => RequireOwner().Logs.PreviewCleanupToLogCount(maxLogCount);

        public long PreviewClearAll() => RequireOwner().Logs.PreviewClearAll();
    }

    private sealed class SelectionPort : IProxySelectionService
    {
        public Task SelectAsync(string groupName, string proxyName, CancellationToken cancellationToken) =>
            RequireOwner().Selections.SelectAsync(groupName, proxyName, cancellationToken);
        public Task RestoreAsync(RuntimeConfigurationActivationPlan plan, CancellationToken cancellationToken) =>
            RequireOwner().Selections.RestoreAsync(plan, cancellationToken);
    }
}

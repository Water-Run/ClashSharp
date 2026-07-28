using System;
using System.Threading;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Per-request, background-safe aggregation work for one runtime snapshot request.</summary>
internal sealed record MasterControlRuntimeSnapshotWork(
    string ActiveProfileId,
    Func<CoreConfigurationState> GetCoreConfiguration,
    Func<string, string?> ReadProfileConfigurationText,
    ProfileCatalogService ProfileCatalog,
    ProfileCatalogFallbackStrings ProfileCatalogFallbackStrings,
    LogStorageService LogStorage,
    int TriggerTaskCount,
    int EnabledTriggerTaskCount,
    MihomoServiceStatus MihomoServiceStatus,
    RuntimeTrafficRateSnapshot RuntimeTraffic,
    Func<StartupRestoreFallbackStatus> GetStartupRestoreFallbackStatus,
    Func<long> GetWorkingSetBytes) : IMasterControlRuntimeSnapshotWork
{
    public MasterControlRuntimeSnapshot Execute(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CoreConfigurationState coreConfiguration = GetCoreConfiguration();
        ProfileCatalogSummary profileSummary = ProfileCatalog.GetSummary(ProfileCatalogFallbackStrings);

        cancellationToken.ThrowIfCancellationRequested();
        (int proxyNodeCount, int ruleCount) = GetActiveProfileCounts();

        cancellationToken.ThrowIfCancellationRequested();
        LogStorageSummary storageSummary = LogStorage.GetStorageSummary();
        TrafficStatisticsSummary trafficSummary = LogStorage.GetTrafficStatisticsSummary();

        cancellationToken.ThrowIfCancellationRequested();
        StartupRestoreFallbackStatus startupRestoreFallbackStatus = GetStartupRestoreFallbackStatus();
        long workingSetBytes = GetWorkingSetBytes();

        cancellationToken.ThrowIfCancellationRequested();
        return new MasterControlRuntimeSnapshot(
            coreConfiguration,
            profileSummary.ProfileCount,
            profileSummary.SubscriptionCount,
            proxyNodeCount,
            ruleCount,
            TriggerTaskCount,
            EnabledTriggerTaskCount,
            storageSummary,
            trafficSummary,
            MihomoServiceStatus,
            startupRestoreFallbackStatus,
            RuntimeTraffic,
            workingSetBytes);
    }

    private (int ProxyNodeCount, int RuleCount) GetActiveProfileCounts()
    {
        string? configurationText = null;
        if (string.IsNullOrWhiteSpace(ActiveProfileId)
            || StringComparer.Ordinal.Equals(ActiveProfileId, ProfileCatalogIds.BuiltInDirect))
        {
            return (
                ProxyNodeCatalogService.CountNodes(configurationText),
                RuleCatalogService.CountRules(configurationText));
        }

        configurationText = ReadProfileConfigurationText(ActiveProfileId);
        return (
            ProxyNodeCatalogService.CountNodes(configurationText),
            RuleCatalogService.CountRules(configurationText));
    }
}

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
    Func<long> GetWorkingSetBytes,
    bool AppCoreRunning = false,
    bool TunRequested = false,
    Func<RuntimeConfigurationIntegrityObservation>? ObserveRuntimeConfigurationIntegrity = null)
    : IMasterControlRuntimeSnapshotWork
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
        RuntimeOwnershipObservation ownership = ObserveRuntimeOwnership(coreConfiguration);

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
            workingSetBytes,
            ownership.IsKnown,
            ownership.Owner,
            TunRequested,
            ownership.TunEffective);
    }

    private RuntimeOwnershipObservation ObserveRuntimeOwnership(CoreConfigurationState coreConfiguration)
    {
        if (!MihomoServiceStatus.IsKnown)
        {
            return RuntimeOwnershipObservation.Unknown;
        }

        RuntimeConfigurationIntegrityObservation integrity =
            ObserveRuntimeConfigurationIntegrity?.Invoke()
            ?? RuntimeConfigurationIntegrityObservation.Unknown;
        if (!integrity.IsKnown)
        {
            return RuntimeOwnershipObservation.Unknown;
        }

        bool serviceCoreRunning = MihomoServiceStatus.IsInstalled && MihomoServiceStatus.IsRunning;
        if (AppCoreRunning && serviceCoreRunning)
        {
            return RuntimeOwnershipObservation.Unknown;
        }

        if (!AppCoreRunning && !serviceCoreRunning)
        {
            return integrity.AppliedPlan is null
                || integrity.AppliedPlan.Mode == ClashSharpMode.Disabled
                ? new RuntimeOwnershipObservation(true, MihomoCoreOwner.None, false)
                : RuntimeOwnershipObservation.Unknown;
        }

        RuntimeConfigurationActivationPlan? plan = integrity.AppliedPlan;
        if (!coreConfiguration.Exists
            || string.IsNullOrWhiteSpace(ActiveProfileId)
            || plan is null
            || !StringComparer.Ordinal.Equals(plan.ProfileId, ActiveProfileId))
        {
            return RuntimeOwnershipObservation.Unknown;
        }

        bool ownerMatches = AppCoreRunning
            ? !plan.TunEnabled && plan.Mode != ClashSharpMode.Disabled
            : plan.TunEnabled
                && plan.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
        if (!ownerMatches)
        {
            return RuntimeOwnershipObservation.Unknown;
        }

        MihomoCoreOwner owner = AppCoreRunning ? MihomoCoreOwner.App : MihomoCoreOwner.Service;
        return new RuntimeOwnershipObservation(true, owner, owner == MihomoCoreOwner.Service);
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

    private readonly record struct RuntimeOwnershipObservation(
        bool IsKnown,
        MihomoCoreOwner Owner,
        bool TunEffective)
    {
        public static RuntimeOwnershipObservation Unknown { get; } =
            new(false, MihomoCoreOwner.None, false);
    }
}

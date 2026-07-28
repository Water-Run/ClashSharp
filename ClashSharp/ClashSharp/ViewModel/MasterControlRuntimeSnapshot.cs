using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Runtime counters and storage summaries shown by master-control information tiles.</summary>
internal sealed record MasterControlRuntimeSnapshot(
    CoreConfigurationState CoreConfiguration,
    int ProfileCount,
    int SubscriptionCount,
    int ProxyNodeCount,
    int RuleCount,
    int TriggerTaskCount,
    int EnabledTriggerTaskCount,
    LogStorageSummary LogStorage,
    TrafficStatisticsSummary Traffic,
    MihomoServiceStatus MihomoService,
    StartupRestoreFallbackStatus StartupRestoreFallback,
    RuntimeTrafficRateSnapshot RuntimeTraffic = default,
    long AppWorkingSetBytes = 0)
{
    public static MasterControlRuntimeSnapshot Unavailable { get; } = new(
        new CoreConfigurationState(string.Empty, string.Empty, false),
        0,
        0,
        0,
        0,
        0,
        0,
        new LogStorageSummary(string.Empty, 0, 0, 0),
        new TrafficStatisticsSummary(0, 0, 0, 0, 0, 0, 0, 0),
        MihomoServiceStatus.Unknown(string.Empty),
        new StartupRestoreFallbackStatus(false, string.Empty));
}

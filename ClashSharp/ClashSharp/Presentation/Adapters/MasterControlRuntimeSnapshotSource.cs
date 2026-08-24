using System;
using ClashSharp.Model;
using ClashSharp.Presentation.Composition;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>
/// Production caller-thread capture for master-control runtime snapshots.
/// </summary>
/// <remarks>
/// The captured work contains immutable caller-thread values plus background-safe delegates and
/// services whose contracts serialize storage access. It never retains a localization service.
/// </remarks>
internal sealed class MasterControlRuntimeSnapshotSource : IMasterControlRuntimeSnapshotSource
{
    private readonly Func<string> _getActiveProfileId;
    private readonly Func<CoreConfigurationState> _getCoreConfiguration;
    private readonly Func<string, string?> _readProfileConfigurationText;
    private readonly ProfileCatalogService _profileCatalog;
    private readonly LogStorageService _logStorage;
    private readonly Func<string, string> _getString;
    private readonly Func<MihomoServiceStatus> _getMihomoServiceStatus;
    private readonly Func<StartupRestoreFallbackStatus> _getStartupRestoreFallbackStatus;
    private readonly Func<RuntimeTrafficRateSnapshot> _getRuntimeTrafficRate;
    private readonly Func<TriggerPresentationSummary> _getTriggerSummary;
    private readonly Func<long> _getWorkingSetBytes;
    private readonly Func<bool> _getAppCoreRunning;
    private readonly Func<bool> _getTunRequested;
    private readonly Func<RuntimeConfigurationIntegrityObservation> _observeRuntimeConfigurationIntegrity;

    internal MasterControlRuntimeSnapshotSource(
        Func<string> getActiveProfileId,
        Func<CoreConfigurationState> getCoreConfiguration,
        Func<string, string?> readProfileConfigurationText,
        ProfileCatalogService profileCatalog,
        LogStorageService logStorage,
        Func<string, string> getString,
        Func<MihomoServiceStatus> getMihomoServiceStatus,
        Func<StartupRestoreFallbackStatus> getStartupRestoreFallbackStatus,
        Func<RuntimeTrafficRateSnapshot> getRuntimeTrafficRate,
        Func<TriggerPresentationSummary> getTriggerSummary,
        Func<long> getWorkingSetBytes,
        Func<bool>? getAppCoreRunning = null,
        Func<bool>? getTunRequested = null,
        Func<RuntimeConfigurationIntegrityObservation>? observeRuntimeConfigurationIntegrity = null)
    {
        _getActiveProfileId = getActiveProfileId ?? throw new ArgumentNullException(nameof(getActiveProfileId));
        _getCoreConfiguration = getCoreConfiguration ?? throw new ArgumentNullException(nameof(getCoreConfiguration));
        _readProfileConfigurationText = readProfileConfigurationText
            ?? throw new ArgumentNullException(nameof(readProfileConfigurationText));
        _profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _getMihomoServiceStatus = getMihomoServiceStatus
            ?? throw new ArgumentNullException(nameof(getMihomoServiceStatus));
        _getStartupRestoreFallbackStatus = getStartupRestoreFallbackStatus
            ?? throw new ArgumentNullException(nameof(getStartupRestoreFallbackStatus));
        _getRuntimeTrafficRate = getRuntimeTrafficRate ?? throw new ArgumentNullException(nameof(getRuntimeTrafficRate));
        _getTriggerSummary = getTriggerSummary ?? throw new ArgumentNullException(nameof(getTriggerSummary));
        _getWorkingSetBytes = getWorkingSetBytes ?? throw new ArgumentNullException(nameof(getWorkingSetBytes));
        _getAppCoreRunning = getAppCoreRunning ?? (static () => false);
        _getTunRequested = getTunRequested ?? (static () => false);
        _observeRuntimeConfigurationIntegrity = observeRuntimeConfigurationIntegrity
            ?? (static () => RuntimeConfigurationIntegrityObservation.Unknown);
    }

    public IMasterControlRuntimeSnapshotWork Capture()
    {
        string activeProfileId = _getActiveProfileId();
        TriggerPresentationSummary triggerSummary = _getTriggerSummary();

        return new MasterControlRuntimeSnapshotWork(
            activeProfileId,
            _getCoreConfiguration,
            _readProfileConfigurationText,
            _profileCatalog,
            new ProfileCatalogFallbackStrings(
                _getString("ProfileCatalog.BuiltInDirect.Name"),
                _getString("ProfileCatalog.Status.Available")),
            _logStorage,
            triggerSummary.TaskCount,
            triggerSummary.EnabledTaskCount,
            _getMihomoServiceStatus(),
            _getRuntimeTrafficRate(),
            _getStartupRestoreFallbackStatus,
            _getWorkingSetBytes,
            _getAppCoreRunning(),
            _getTunRequested(),
            _observeRuntimeConfigurationIntegrity);
    }
}

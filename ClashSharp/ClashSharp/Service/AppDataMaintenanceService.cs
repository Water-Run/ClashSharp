/*
 * Application Data Maintenance Service
 * Provides destructive user-requested reset operations for local Clash# data
 *
 * @author: WaterRun
 * @file: Service/AppDataMaintenanceService.cs
 * @date: 2026-06-17
 */

using System;
using System.IO;

namespace ClashSharp.Service;

/// <summary>Resets persisted application settings.</summary>
internal interface IAppDataMaintenanceSettings
{
    /// <summary>Resets all settings to defaults.</summary>
    void ResetAllSettings();
}

/// <summary>Coordinates runtime shutdown before deleting application data.</summary>
internal interface IAppDataMaintenanceRuntime
{
    /// <summary>Stops owned runtime services.</summary>
    void Shutdown();
}

/// <summary>Clears and resets log storage during data maintenance.</summary>
internal interface IAppDataMaintenanceLogStorage
{
    /// <summary>Clears persisted logs.</summary>
    void ClearAll();

    /// <summary>Resets log storage state after data deletion.</summary>
    void ResetAfterDataDeletion();

    /// <summary>Appends a maintenance log entry.</summary>
    void AppendLog(string level, string category, string message, string? detail);
}

/// <summary>Deletes local application data files.</summary>
internal interface IAppDataMaintenanceLocalDataStore
{
    /// <summary>Deletes local application data files and directories.</summary>
    void ClearAll();
}

/// <summary>Resets profile catalog caches after data deletion.</summary>
internal interface IAppDataMaintenanceProfileCatalog
{
    /// <summary>Forgets cached profile catalog state.</summary>
    void ResetAfterDataDeletion();
}

/// <summary>Provides destructive user-requested reset operations for local Clash# data.</summary>
/// <remarks>
/// Invariants: Clear-all operations are scoped to the local application data directory.
/// Thread safety: Not thread-safe; intended for user-triggered maintenance on the UI thread.
/// Side effects: Stops runtime services, resets settings, and deletes local data files.
/// </remarks>
internal sealed partial class AppDataMaintenanceService
{
    private readonly IAppDataMaintenanceSettings _settings;

    private readonly IAppDataMaintenanceRuntime _runtime;

    private readonly IAppDataMaintenanceLogStorage _logStorage;

    private readonly IAppDataMaintenanceLocalDataStore _localData;

    private readonly IAppDataMaintenanceProfileCatalog _profiles;

    private readonly Func<string, string> _getString;

    internal AppDataMaintenanceService(
        IAppDataMaintenanceSettings settings,
        IAppDataMaintenanceRuntime runtime,
        IAppDataMaintenanceLogStorage logStorage,
        IAppDataMaintenanceLocalDataStore localData,
        IAppDataMaintenanceProfileCatalog profiles,
        Func<string, string> getString)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
        _localData = localData ?? throw new ArgumentNullException(nameof(localData));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Resets all persisted settings to their default values.</summary>
    public void ResetSettings()
    {
        _settings.ResetAllSettings();
    }

    /// <summary>Clears all user data including settings, logs, profiles, and generated mihomo configuration.</summary>
    public void ClearData()
    {
        _runtime.Shutdown();
        _settings.ResetAllSettings();
        TryClearLogStorage();
        _localData.ClearAll();
        _logStorage.ResetAfterDataDeletion();
        _profiles.ResetAfterDataDeletion();
    }

    /// <summary>Clears log storage when the database can be opened before file deletion.</summary>
    private void TryClearLogStorage()
    {
        try
        {
            _logStorage.ClearAll();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logStorage.AppendLog("Warning", "Maintenance", GetString("Maintenance.LogClearFailed"), exception.Message);
        }
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}

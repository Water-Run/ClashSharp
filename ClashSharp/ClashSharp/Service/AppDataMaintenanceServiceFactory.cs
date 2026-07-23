using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Hosting.Compatibility;

namespace ClashSharp.Service;

internal sealed partial class AppDataMaintenanceService
{
    /// <summary>Shared singleton instance.</summary>
    public static AppDataMaintenanceService Instance { get; } = AppDataMaintenanceServiceFactory.CreateDefault();

    /// <summary>Resets all persisted settings to their default values.</summary>
    public static void ResetAllSettings()
    {
        Instance.ResetSettings();
    }

    /// <summary>Clears all user data including settings, logs, profiles, and generated mihomo configuration.</summary>
    public static Task ClearAllDataAsync(CancellationToken cancellationToken)
    {
        return Instance.ClearDataAsync(cancellationToken);
    }
}

/// <summary>Creates application data maintenance services with production dependencies.</summary>
internal static class AppDataMaintenanceServiceFactory
{
    /// <summary>Creates the default maintenance service used by settings.</summary>
    public static AppDataMaintenanceService CreateDefault()
    {
        AppDataMaintenanceLogStorageAdapter logStorage = new(LogStorageService.Instance);
        return new AppDataMaintenanceService(
            new AppDataMaintenanceSettingsAdapter(AppSettingsService.Instance),
            new LegacyAppDataMaintenanceRuntimeAdapter(
                ConnectionSamplingService.Instance,
                MihomoCoreService.Instance,
                AppSettingsService.Instance,
                WindowsProxyService.Instance,
                LogStorageService.Instance,
                LocalizationService.Instance.GetString),
            logStorage,
            new AppDataMaintenanceLocalDataStore(logStorage, LocalizationService.Instance.GetString),
            new AppDataMaintenanceProfileCatalogAdapter(ProfileCatalogService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class AppDataMaintenanceSettingsAdapter(AppSettingsService settings) : IAppDataMaintenanceSettings
{
    public void ResetAllSettings()
    {
        settings.ResetAllSettings();
    }
}

internal sealed class AppDataMaintenanceLogStorageAdapter(LogStorageService logStorage) : IAppDataMaintenanceLogStorage
{
    public void ClearAll()
    {
        logStorage.ClearAll();
    }

    public void ResetAfterDataDeletion()
    {
        logStorage.ResetAfterDataDeletion();
    }

    public void AppendLog(string level, string category, string message, string? detail)
    {
        logStorage.AppendLog(level, category, message, detail);
    }
}

internal sealed class AppDataMaintenanceLocalDataStore(
    IAppDataMaintenanceLogStorage logStorage,
    Func<string, string> getString) : IAppDataMaintenanceLocalDataStore
{
    public void ClearAll()
    {
        string dataDirectory = Path.GetFullPath(AppDataPathService.ResolveLocalDataDirectory());
        Directory.CreateDirectory(dataDirectory);

        foreach (string filePath in Directory.EnumerateFiles(dataDirectory))
        {
            TryDeleteFile(filePath);
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(dataDirectory))
        {
            TryDeleteDirectory(directoryPath);
        }
    }

    private void TryDeleteFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            File.Delete(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logStorage.AppendLog("Warning", "Maintenance", getString("Maintenance.FileDeleteFailed"), filePath);
        }
    }

    private void TryDeleteDirectory(string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);

        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logStorage.AppendLog("Warning", "Maintenance", getString("Maintenance.DirectoryDeleteFailed"), directoryPath);
        }
    }
}

internal sealed class AppDataMaintenanceProfileCatalogAdapter(ProfileCatalogService profiles) : IAppDataMaintenanceProfileCatalog
{
    public void ResetAfterDataDeletion()
    {
        profiles.ResetAfterDataDeletion();
    }
}

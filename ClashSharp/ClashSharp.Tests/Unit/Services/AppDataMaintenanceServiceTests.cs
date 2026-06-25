/*
 * App Data Maintenance Service Tests
 * Verifies destructive maintenance orchestration through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/AppDataMaintenanceServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for user-triggered application data maintenance.</summary>
public sealed class AppDataMaintenanceServiceTests
{
    /// <summary>Verifies resetting settings delegates only to the settings store.</summary>
    [Fact]
    public void ResetSettings_ResetsSettingsOnly()
    {
        List<string> calls = [];
        AppDataMaintenanceService service = CreateService(calls);

        service.ResetSettings();

        Assert.Equal(["settings.reset"], calls);
    }

    /// <summary>Verifies clearing all data performs shutdown, reset, storage cleanup, local deletion, and cache resets in order.</summary>
    [Fact]
    public void ClearData_RunsMaintenanceStepsInOrder()
    {
        List<string> calls = [];
        AppDataMaintenanceService service = CreateService(calls);

        service.ClearData();

        Assert.Equal(
            [
                "runtime.shutdown",
                "settings.reset",
                "logs.clear",
                "local.clear",
                "logs.reset",
                "profiles.reset",
            ],
            calls);
    }

    /// <summary>Verifies log-storage clear failures are localized and do not stop data deletion.</summary>
    [Fact]
    public void ClearData_WhenLogClearFails_LogsWarningAndContinues()
    {
        List<string> calls = [];
        FakeAppDataMaintenanceLogStorage logStorage = new(calls)
        {
            ClearException = new IOException("database locked"),
        };
        AppDataMaintenanceService service = CreateService(calls, logStorage: logStorage);

        service.ClearData();

        Assert.Contains("local.clear", calls);
        AppDataMaintenanceLogEntry entry = Assert.Single(logStorage.Entries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("Maintenance", entry.Category);
        Assert.Equal("localized log clear failed", entry.Message);
        Assert.Equal("database locked", entry.Detail);
    }

    private static AppDataMaintenanceService CreateService(
        List<string> calls,
        FakeAppDataMaintenanceLogStorage? logStorage = null)
    {
        return new AppDataMaintenanceService(
            new FakeAppDataMaintenanceSettings(calls),
            new FakeAppDataMaintenanceRuntime(calls),
            logStorage ?? new FakeAppDataMaintenanceLogStorage(calls),
            new FakeAppDataMaintenanceLocalData(calls),
            new FakeAppDataMaintenanceProfileCatalog(calls),
            key => key == "Maintenance.LogClearFailed" ? "localized log clear failed" : key);
    }

    private sealed class FakeAppDataMaintenanceSettings(List<string> calls) : IAppDataMaintenanceSettings
    {
        public void ResetAllSettings()
        {
            calls.Add("settings.reset");
        }
    }

    private sealed class FakeAppDataMaintenanceRuntime(List<string> calls) : IAppDataMaintenanceRuntime
    {
        public void Shutdown()
        {
            calls.Add("runtime.shutdown");
        }
    }

    private sealed class FakeAppDataMaintenanceLogStorage(List<string> calls) : IAppDataMaintenanceLogStorage
    {
        public Exception? ClearException { get; init; }

        public List<AppDataMaintenanceLogEntry> Entries { get; } = [];

        public void ClearAll()
        {
            calls.Add("logs.clear");
            if (ClearException is not null)
            {
                throw ClearException;
            }
        }

        public void ResetAfterDataDeletion()
        {
            calls.Add("logs.reset");
        }

        public void AppendLog(string level, string category, string message, string? detail)
        {
            Entries.Add(new AppDataMaintenanceLogEntry(level, category, message, detail));
        }
    }

    private sealed class FakeAppDataMaintenanceLocalData(List<string> calls) : IAppDataMaintenanceLocalDataStore
    {
        public void ClearAll()
        {
            calls.Add("local.clear");
        }
    }

    private sealed class FakeAppDataMaintenanceProfileCatalog(List<string> calls) : IAppDataMaintenanceProfileCatalog
    {
        public void ResetAfterDataDeletion()
        {
            calls.Add("profiles.reset");
        }
    }

    private readonly record struct AppDataMaintenanceLogEntry(string Level, string Category, string Message, string? Detail);
}

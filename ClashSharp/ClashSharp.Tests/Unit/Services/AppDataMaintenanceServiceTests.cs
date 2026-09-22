using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Security;
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
    public async Task ClearDataAsync_RunsMaintenanceStepsInOrder()
    {
        List<string> calls = [];
        AppDataMaintenanceService service = CreateService(calls);

        await service.ClearDataAsync(CancellationToken.None);

        Assert.Equal(
            [
                "runtime.shutdown",
                "settings.clear",
                "credentials.clear",
                "logs.clear",
                "local.clear",
                "logs.reset",
                "profiles.reset",
            ],
            calls);
    }

    /// <summary>Verifies log-storage clear failures are localized and do not stop data deletion.</summary>
    [Fact]
    public async Task ClearDataAsync_WhenLogClearFails_LogsWarningAndContinues()
    {
        List<string> calls = [];
        FakeAppDataMaintenanceLogStorage logStorage = new(calls)
        {
            ClearException = new IOException("database locked"),
        };
        AppDataMaintenanceService service = CreateService(calls, logStorage: logStorage);

        await service.ClearDataAsync(CancellationToken.None);

        Assert.Contains("local.clear", calls);
        AppDataMaintenanceLogEntry entry = Assert.Single(logStorage.Entries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("Maintenance", entry.Category);
        Assert.Equal("localized log clear failed", entry.Message);
        Assert.Equal("database locked", entry.Detail);
    }

    /// <summary>Verifies cancellation after runtime shutdown still protects persisted user data from deletion.</summary>
    [Fact]
    public async Task ClearDataAsync_WhenCancelledAfterShutdown_DoesNotDeleteData()
    {
        List<string> calls = [];
        using CancellationTokenSource cancellation = new();
        AppDataMaintenanceService service = CreateService(
            calls,
            runtime: new CancellingAppDataMaintenanceRuntime(calls, cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ClearDataAsync(cancellation.Token));

        Assert.Equal(["runtime.shutdown"], calls);
    }

    [Fact]
    public async Task PreferenceReset_PreservesCredentialsAndFullDeletionRemovesThemAfterShutdown()
    {
        List<string> calls = [];
        ControllerCredentialTestStore store = new() { Present = true, Value = ControllerCredentialTestStore.ExistingSecret };
        store.BeforeDelete = () => { Assert.Contains("runtime.shutdown", calls); calls.Add("credentials.clear"); };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService owner = new(store, admission);
        using (MutationAdmissionLease lease = admission.AcquireOrdinary()) { owner.InitializeAdmitted(lease, CancellationToken.None); }
        MihomoControllerCredentials credentials = new();
        credentials.Bind(owner, admission);
        AppDataMaintenanceService service = CreateService(calls, credentials: credentials);
        service.ResetSettings();
        Assert.Equal(ControllerCredentialTestStore.ExistingSecret, credentials.GetSecret());
        Assert.Equal(0, store.Deletes);
        calls.Clear();
        await service.ClearDataAsync(CancellationToken.None);
        Assert.Equal(["runtime.shutdown", "settings.clear", "credentials.clear", "logs.clear", "local.clear", "logs.reset", "profiles.reset"], calls);
        Assert.False(store.Present);
        Assert.Throws<ControllerCredentialException>(credentials.GetSecret);
    }

    [Fact]
    public async Task TerminalDataDeletion_UsesMaintenanceAdmissionForTheIndependentCredentialSlot()
    {
        List<string> calls = [];
        ControllerCredentialTestStore store = new() { Present = true, Value = ControllerCredentialTestStore.ExistingSecret };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService owner = new(store, admission);
        using (MutationAdmissionLease lease = admission.AcquireOrdinary()) { owner.InitializeAdmitted(lease, CancellationToken.None); }
        MihomoControllerCredentials credentials = new();
        credentials.Bind(owner, admission);
        await using (MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None)) { lease.CommitShutdown(); }
        store.BeforeDelete = () => calls.Add("credentials.clear");
        AppDataMaintenanceService service = CreateService(calls, credentials: credentials);
        service.ClearDataAfterRuntimeShutdown(CancellationToken.None, useTerminalSettingsAdmission: true);
        Assert.Equal(["settings.clear-terminal", "credentials.clear", "logs.clear", "local.clear", "logs.reset", "profiles.reset"], calls);
        Assert.False(store.Present);
        Assert.Throws<MutationAdmissionRejectedException>(() => admission.AcquireOrdinary());
    }

    [Fact]
    public async Task UnverifiedCredentialDeletion_StopsBeforeDeletingTheRemainingData()
    {
        List<string> calls = [];
        ControllerCredentialTestStore store = new() { Present = true, Value = ControllerCredentialTestStore.ExistingSecret, IgnoreDeletes = true };
        store.BeforeDelete = () => calls.Add("credentials.clear");
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService owner = new(store, admission);
        using (MutationAdmissionLease lease = admission.AcquireOrdinary()) { owner.InitializeAdmitted(lease, CancellationToken.None); }
        MihomoControllerCredentials credentials = new();
        credentials.Bind(owner, admission);
        AppDataMaintenanceService service = CreateService(calls, credentials: credentials);
        await Assert.ThrowsAsync<ControllerCredentialException>(() => service.ClearDataAsync(CancellationToken.None));
        Assert.Equal(["runtime.shutdown", "settings.clear", "credentials.clear"], calls);
        Assert.True(store.Present);
    }

    [Fact]
    public async Task CancellationAfterPreferenceDeletion_StillCompletesCredentialAndFileCleanup()
    {
        List<string> calls = [];
        using CancellationTokenSource cancellation = new();
        AppDataMaintenanceService service = CreateService(calls,
            settings: new CancellingSettings(calls, cancellation));
        await service.ClearDataAsync(cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(["runtime.shutdown", "settings.clear", "credentials.clear", "logs.clear", "local.clear", "logs.reset", "profiles.reset"], calls);
    }

    private static AppDataMaintenanceService CreateService(
        List<string> calls,
        FakeAppDataMaintenanceLogStorage? logStorage = null,
        IAppDataMaintenanceRuntime? runtime = null,
        IAppDataMaintenanceCredentials? credentials = null,
        IAppDataMaintenanceSettings? settings = null)
    {
        return new AppDataMaintenanceService(
            settings ?? new FakeAppDataMaintenanceSettings(calls),
            credentials ?? new FakeCredentials(calls),
            runtime ?? new FakeAppDataMaintenanceRuntime(calls),
            logStorage ?? new FakeAppDataMaintenanceLogStorage(calls),
            new FakeAppDataMaintenanceLocalData(calls),
            new FakeAppDataMaintenanceProfileCatalog(calls),
            key => key == "Maintenance.LogClearFailed" ? "localized log clear failed" : key);
    }

    private sealed class FakeAppDataMaintenanceSettings(List<string> calls) : IAppDataMaintenanceSettings, ITerminalShutdownSettingsMaintenance
    {
        public void ResetAllSettings()
        {
            calls.Add("settings.reset");
        }

        public void ClearAllSettings()
        {
            calls.Add("settings.clear");
        }

        public void ClearAllSettingsAfterShutdown() => calls.Add("settings.clear-terminal");
    }

    private sealed class FakeCredentials(List<string> calls) : IAppDataMaintenanceCredentials
    {
        public void ClearAll(bool useTerminalAdmission, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("credentials.clear");
        }
    }

    private sealed class CancellingSettings(List<string> calls, CancellationTokenSource cancellation) : IAppDataMaintenanceSettings
    {
        public void ResetAllSettings() => calls.Add("settings.reset");
        public void ClearAllSettings() { calls.Add("settings.clear"); cancellation.Cancel(); }
    }

    private sealed class FakeAppDataMaintenanceRuntime(List<string> calls) : IAppDataMaintenanceRuntime
    {
        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("runtime.shutdown");
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingAppDataMaintenanceRuntime(
        List<string> calls,
        CancellationTokenSource cancellation) : IAppDataMaintenanceRuntime
    {
        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            calls.Add("runtime.shutdown");
            cancellation.Cancel();
            return Task.CompletedTask;
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

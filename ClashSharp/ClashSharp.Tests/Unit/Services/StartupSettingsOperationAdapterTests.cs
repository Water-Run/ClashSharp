using System.Reflection;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Composes production startup adapters with an in-memory settings authority and simulated Windows task.</summary>
public sealed class StartupSettingsOperationAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedRegistration_IsPublishedUnderAdmission(bool baseline)
    {
        MutationAdmissionBarrier admission = new();
        AppSettingsService settings = PrepareSettings(admission, baseline);
        PlatformProbe platform = new() { State = baseline ? StartupLaunchTaskState.Enabled : StartupLaunchTaskState.Disabled };
        StartupSettingsCoordinator coordinator = CreateCoordinator(admission, settings, platform);
        Task<MutationAdmissionLease>? drain = null;
        int notifications = 0;
        void Changed(object? sender, AppSettingChangedEventArgs change)
        {
            Assert.Equal("LaunchAtStartupEnabled", change.Key);
            Assert.Equal(!baseline, settings.LaunchAtStartupEnabled);
            Assert.Equal(baseline ? StartupLaunchTaskState.Disabled : StartupLaunchTaskState.Enabled, platform.State);
            drain = admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
            Assert.False(drain.IsCompleted);
            ++notifications;
        }

        settings.SettingChanged += Changed;
        try
        {
            await coordinator.ApplyAsync(!baseline, CancellationToken.None);
            Assert.Equal(1, notifications);
            await using MutationAdmissionLease exclusive = await Assert.IsAssignableFrom<Task<MutationAdmissionLease>>(drain);
        }
        finally
        {
            settings.SettingChanged -= Changed;
            settings.LaunchAtStartupEnabled = baseline;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialEnableFailure_RestoresTheObservedTaskIndependentlyOfPreference(bool preference)
    {
        MutationAdmissionBarrier admission = new();
        AppSettingsService settings = PrepareSettings(admission, preference);
        PlatformProbe platform = new() { FailAfterEnable = true };

        StartupLaunchUpdateException result = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => CreateCoordinator(admission, settings, platform).ApplyAsync(true, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.PlatformFailure, result.Failure);
        Assert.Equal(preference, settings.LaunchAtStartupEnabled);
        Assert.Equal(StartupLaunchTaskState.Disabled, platform.State);
        Assert.Equal(1, platform.EnableCalls);
        Assert.Equal(1, platform.DisableCalls);
    }

    [Fact]
    public async Task DeniedEnable_PreservesTheOriginalPreferenceAndTask()
    {
        MutationAdmissionBarrier admission = new();
        AppSettingsService settings = PrepareSettings(admission, false);
        PlatformProbe platform = new() { DenyEnable = true };

        StartupLaunchUpdateException result = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => CreateCoordinator(admission, settings, platform).ApplyAsync(true, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.EnableDenied, result.Failure);
        Assert.False(settings.LaunchAtStartupEnabled);
        Assert.Equal(StartupLaunchTaskState.Disabled, platform.State);
        Assert.Equal(0, platform.DisableCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedOrUnavailableTask_DoesNotChangeEitherParticipant(bool unavailable)
    {
        MutationAdmissionBarrier admission = new();
        AppSettingsService settings = PrepareSettings(admission, false);
        PlatformProbe platform = new() { State = StartupLaunchTaskState.Other, Unavailable = unavailable };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateCoordinator(admission, settings, platform).ApplyAsync(true, CancellationToken.None));

        Assert.False(settings.LaunchAtStartupEnabled);
        Assert.Equal(StartupLaunchTaskState.Other, platform.State);
        Assert.Equal(0, platform.EnableCalls);
        Assert.Equal(0, platform.DisableCalls);
    }

    private static AppSettingsService PrepareSettings(MutationAdmissionBarrier admission, bool preference)
    {
        AppSettingsService settings = AppSettingsService.Instance;
        Assert.Null(typeof(AppSettingsService).GetField("_localSettings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings));
        settings.ConfigureMutationAdmission(admission);
        settings.LaunchAtStartupEnabled = preference;
        return settings;
    }

    private static StartupSettingsCoordinator CreateCoordinator(
        MutationAdmissionBarrier admission,
        AppSettingsService settings,
        PlatformProbe platform) =>
        new(admission, new StartupSettingsOperationAdapter(settings, new StartupLaunchService(platform, platform, key => key)));

    private sealed class PlatformProbe : IStartupLaunchTaskProvider, IStartupLaunchTask, IStartupLaunchLog
    {
        public StartupLaunchTaskState State { get; set; } = StartupLaunchTaskState.Disabled;
        public bool FailAfterEnable { get; init; }
        public bool DenyEnable { get; init; }
        public bool Unavailable { get; init; }
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }

        public Task<IStartupLaunchTask> GetAsync(string taskId)
        {
            Assert.Equal(StartupLaunchService.TaskId, taskId);
            return Unavailable
                ? Task.FromException<IStartupLaunchTask>(new InvalidOperationException("platform unavailable"))
                : Task.FromResult<IStartupLaunchTask>(this);
        }

        public Task<StartupLaunchTaskState> RequestEnableAsync()
        {
            ++EnableCalls;
            State = DenyEnable ? StartupLaunchTaskState.Disabled : StartupLaunchTaskState.Enabled;
            return FailAfterEnable
                ? Task.FromException<StartupLaunchTaskState>(new InvalidOperationException("platform changed before failure"))
                : Task.FromResult(State);
        }

        public void Disable()
        {
            ++DisableCalls;
            State = StartupLaunchTaskState.Disabled;
        }

        public void AppendLog(string level, string category, string message, string? detail)
        {
        }
    }
}

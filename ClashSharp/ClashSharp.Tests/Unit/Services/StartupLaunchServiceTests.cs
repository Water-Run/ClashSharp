/*
 * Startup Launch Service Tests
 * Verifies startup task coordination through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/StartupLaunchServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for launch-at-startup synchronization.</summary>
public sealed class StartupLaunchServiceTests
{
    /// <summary>Verifies enabling launch-at-startup requests enablement only when the task is disabled.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenEnabledRequestedAndTaskDisabled_RequestsEnable()
    {
        FakeStartupLaunchTask task = new(StartupLaunchTaskState.Disabled);
        StartupLaunchService service = CreateService(task);

        await service.SetEnabledAsync(true);

        Assert.Equal(1, task.RequestEnableCount);
        Assert.Equal(0, task.DisableCount);
    }

    /// <summary>Verifies disabling launch-at-startup disables the task only when it is enabled.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenDisabledRequestedAndTaskEnabled_DisablesTask()
    {
        FakeStartupLaunchTask task = new(StartupLaunchTaskState.Enabled);
        StartupLaunchService service = CreateService(task);

        await service.SetEnabledAsync(false);

        Assert.Equal(0, task.RequestEnableCount);
        Assert.Equal(1, task.DisableCount);
    }

    /// <summary>Verifies startup task API failures are logged with localized text and do not escape settings writes.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenTaskProviderFails_LogsLocalizedWarning()
    {
        FakeStartupLaunchLog log = new();
        StartupLaunchService service = CreateService(exception: new InvalidOperationException("startup task unavailable"), log: log);

        await service.SetEnabledAsync(true);

        StartupLaunchLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("StartupLaunch", entry.Category);
        Assert.Equal("localized update failed", entry.Message);
        Assert.Equal("startup task unavailable", entry.Detail);
    }

    private static StartupLaunchService CreateService(
        FakeStartupLaunchTask? task = null,
        Exception? exception = null,
        FakeStartupLaunchLog? log = null)
    {
        return new StartupLaunchService(
            new FakeStartupLaunchTaskProvider(task ?? new FakeStartupLaunchTask(StartupLaunchTaskState.Disabled), exception),
            log ?? new FakeStartupLaunchLog(),
            key => key == "StartupLaunch.UpdateFailed" ? "localized update failed" : key);
    }

    private sealed class FakeStartupLaunchTaskProvider(IStartupLaunchTask task, Exception? exception) : IStartupLaunchTaskProvider
    {
        public Task<IStartupLaunchTask> GetAsync(string taskId)
        {
            if (exception is not null)
            {
                throw exception;
            }

            Assert.Equal(StartupLaunchService.TaskId, taskId);
            return Task.FromResult(task);
        }
    }

    private sealed class FakeStartupLaunchTask(StartupLaunchTaskState state) : IStartupLaunchTask
    {
        public StartupLaunchTaskState State { get; } = state;

        public int RequestEnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public Task RequestEnableAsync()
        {
            RequestEnableCount++;
            return Task.CompletedTask;
        }

        public void Disable()
        {
            DisableCount++;
        }
    }

    private sealed class FakeStartupLaunchLog : IStartupLaunchLog
    {
        public List<StartupLaunchLogEntry> Entries { get; } = [];

        public void AppendLog(string level, string category, string message, string? detail)
        {
            Entries.Add(new StartupLaunchLogEntry(level, category, message, detail));
        }
    }

    private readonly record struct StartupLaunchLogEntry(string Level, string Category, string Message, string? Detail);
}

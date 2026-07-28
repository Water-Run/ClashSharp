using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for launch-at-startup synchronization.</summary>
public sealed class StartupLaunchServiceTests
{
    /// <summary>Verifies reconciliation can inspect the actual packaged startup-task state.</summary>
    [Theory]
    [InlineData((int)StartupLaunchTaskState.Disabled)]
    [InlineData((int)StartupLaunchTaskState.Enabled)]
    [InlineData((int)StartupLaunchTaskState.Other)]
    public async Task TryGetStateAsync_ReturnsObservedPlatformState(int stateValue)
    {
        StartupLaunchTaskState state = (StartupLaunchTaskState)stateValue;
        StartupLaunchService service = CreateService(new FakeStartupLaunchTask(state));

        StartupLaunchTaskState? observed = await service.TryGetStateAsync(CancellationToken.None);

        Assert.Equal(state, observed);
    }

    /// <summary>Verifies an unavailable startup API is represented as unknown and remains diagnosable.</summary>
    [Fact]
    public async Task TryGetStateAsync_WhenProviderFails_ReturnsUnknownAndLogsWarning()
    {
        FakeStartupLaunchLog log = new();
        StartupLaunchService service = CreateService(
            exception: new InvalidOperationException("startup task unavailable"),
            log: log);

        StartupLaunchTaskState? observed = await service.TryGetStateAsync(CancellationToken.None);

        Assert.Null(observed);
        Assert.Equal("startup task unavailable", Assert.Single(log.Entries).Detail);
    }

    /// <summary>Verifies enabling launch-at-startup requests enablement only when the task is disabled.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenEnabledRequestedAndTaskDisabled_RequestsEnable()
    {
        FakeStartupLaunchTask task = new(
            StartupLaunchTaskState.Disabled,
            requestEnableState: StartupLaunchTaskState.Enabled,
            stateAfterRequest: StartupLaunchTaskState.Enabled);
        StartupLaunchService service = CreateService(task);

        await service.SetEnabledAsync(true, CancellationToken.None);

        Assert.Equal(1, task.RequestEnableCount);
        Assert.Equal(0, task.DisableCount);
    }

    /// <summary>Verifies disabling launch-at-startup disables the task only when it is enabled.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenDisabledRequestedAndTaskEnabled_DisablesTask()
    {
        FakeStartupLaunchTask task = new(
            StartupLaunchTaskState.Enabled,
            stateAfterDisable: StartupLaunchTaskState.Disabled);
        StartupLaunchService service = CreateService(task);

        await service.SetEnabledAsync(false, CancellationToken.None);

        Assert.Equal(0, task.RequestEnableCount);
        Assert.Equal(1, task.DisableCount);
    }

    /// <summary>Verifies provider failures are logged and escape as a classified update failure.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenTaskProviderFails_LogsAndThrowsTypedFailure()
    {
        FakeStartupLaunchLog log = new();
        StartupLaunchService service = CreateService(exception: new InvalidOperationException("startup task unavailable"), log: log);

        StartupLaunchUpdateException exception = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => service.SetEnabledAsync(true, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.PlatformFailure, exception.Failure);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        StartupLaunchLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("StartupLaunch", entry.Category);
        Assert.Equal("localized update failed", entry.Message);
        Assert.Equal("startup task unavailable", entry.Detail);
    }

    /// <summary>Verifies an explicit Windows enable denial is not reported as success.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenEnableRequestIsDenied_LogsAndThrowsTypedFailure()
    {
        FakeStartupLaunchLog log = new();
        FakeStartupLaunchTask task = new(
            StartupLaunchTaskState.Disabled,
            requestEnableState: StartupLaunchTaskState.Disabled);
        StartupLaunchService service = CreateService(task, log: log);

        StartupLaunchUpdateException exception = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => service.SetEnabledAsync(true, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.EnableDenied, exception.Failure);
        Assert.Equal(StartupLaunchTaskState.Disabled, exception.ObservedState);
        Assert.Single(log.Entries);
    }

    /// <summary>Verifies platform-specific states do not silently accept an update.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenTaskIsInOtherState_LogsAndThrowsTypedFailure()
    {
        FakeStartupLaunchLog log = new();
        StartupLaunchService service = CreateService(
            new FakeStartupLaunchTask(StartupLaunchTaskState.Other),
            log: log);

        StartupLaunchUpdateException exception = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => service.SetEnabledAsync(true, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.UnsupportedState, exception.Failure);
        Assert.Equal(StartupLaunchTaskState.Other, exception.ObservedState);
        Assert.Single(log.Entries);
    }

    /// <summary>Verifies a platform-specific enable response is classified even when the initial state was mutable.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenEnableRequestReturnsOther_LogsAndThrowsTypedFailure()
    {
        FakeStartupLaunchLog log = new();
        FakeStartupLaunchTask task = new(
            StartupLaunchTaskState.Disabled,
            requestEnableState: StartupLaunchTaskState.Other);
        StartupLaunchService service = CreateService(task, log: log);

        StartupLaunchUpdateException exception = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => service.SetEnabledAsync(true, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.UnsupportedState, exception.Failure);
        Assert.Equal(StartupLaunchTaskState.Other, exception.ObservedState);
        Assert.Equal(1, task.RequestEnableCount);
        Assert.Single(log.Entries);
    }

    /// <summary>Verifies a nominal enable response is checked against a fresh observed state.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenEnableVerificationFails_LogsAndThrowsTypedFailure()
    {
        FakeStartupLaunchLog log = new();
        FakeStartupLaunchTask task = new(
            StartupLaunchTaskState.Disabled,
            requestEnableState: StartupLaunchTaskState.Enabled);
        StartupLaunchService service = CreateService(task, log: log);

        StartupLaunchUpdateException exception = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => service.SetEnabledAsync(true, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.VerificationFailed, exception.Failure);
        Assert.Equal(StartupLaunchTaskState.Disabled, exception.ObservedState);
        Assert.Equal(1, task.RequestEnableCount);
        Assert.Single(log.Entries);
    }

    /// <summary>Verifies disablement is rejected when Windows still reports the task as enabled.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenDisableVerificationStillReportsEnabled_ThrowsTypedFailure()
    {
        FakeStartupLaunchLog log = new();
        FakeStartupLaunchTask task = new(StartupLaunchTaskState.Enabled);
        StartupLaunchService service = CreateService(task, log: log);

        StartupLaunchUpdateException exception = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => service.SetEnabledAsync(false, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.VerificationFailed, exception.Failure);
        Assert.Equal(StartupLaunchTaskState.Enabled, exception.ObservedState);
        Assert.Equal(1, task.DisableCount);
        Assert.Single(log.Entries);
    }

    /// <summary>Verifies an indeterminate state after disablement is not reported as success.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenDisableVerificationReportsOther_ThrowsTypedFailure()
    {
        FakeStartupLaunchLog log = new();
        FakeStartupLaunchTask task = new(
            StartupLaunchTaskState.Enabled,
            stateAfterDisable: StartupLaunchTaskState.Other);
        StartupLaunchService service = CreateService(task, log: log);

        StartupLaunchUpdateException exception = await Assert.ThrowsAsync<StartupLaunchUpdateException>(
            () => service.SetEnabledAsync(false, CancellationToken.None));

        Assert.Equal(StartupLaunchUpdateFailure.VerificationFailed, exception.Failure);
        Assert.Equal(StartupLaunchTaskState.Other, exception.ObservedState);
        Assert.Equal(1, task.DisableCount);
        Assert.Single(log.Entries);
    }

    /// <summary>Verifies cancellation is propagated without invoking or logging a platform update.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenCancelledBeforeUpdate_PropagatesCancellation()
    {
        FakeStartupLaunchLog log = new();
        FakeStartupLaunchTaskProvider provider = new(
            new FakeStartupLaunchTask(StartupLaunchTaskState.Disabled),
            exception: null);
        StartupLaunchService service = new(
            provider,
            log,
            key => key == "StartupLaunch.UpdateFailed" ? "localized update failed" : key);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SetEnabledAsync(true, cancellation.Token));

        Assert.Equal(0, provider.GetCount);
        Assert.Empty(log.Entries);
    }

    /// <summary>Verifies cancellation after provider access prevents the platform mutation and remains unwrapped.</summary>
    [Fact]
    public async Task SetEnabledAsync_WhenCancelledAfterProviderAccess_DoesNotMutateOrLog()
    {
        FakeStartupLaunchLog log = new();
        FakeStartupLaunchTask task = new(StartupLaunchTaskState.Disabled);
        using CancellationTokenSource cancellation = new();
        FakeStartupLaunchTaskProvider provider = new(task, exception: null)
        {
            AfterGet = cancellation.Cancel,
        };
        StartupLaunchService service = new(
            provider,
            log,
            key => key == "StartupLaunch.UpdateFailed" ? "localized update failed" : key);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SetEnabledAsync(true, cancellation.Token));

        Assert.Equal(0, task.RequestEnableCount);
        Assert.Empty(log.Entries);
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
        public int GetCount { get; private set; }

        public Action? AfterGet { get; init; }

        public Task<IStartupLaunchTask> GetAsync(string taskId)
        {
            GetCount++;
            if (exception is not null)
            {
                throw exception;
            }

            Assert.Equal(StartupLaunchService.TaskId, taskId);
            AfterGet?.Invoke();
            return Task.FromResult(task);
        }
    }

    private sealed class FakeStartupLaunchTask : IStartupLaunchTask
    {
        private readonly StartupLaunchTaskState _requestEnableState;

        private readonly StartupLaunchTaskState? _stateAfterRequest;

        private readonly StartupLaunchTaskState? _stateAfterDisable;

        public FakeStartupLaunchTask(
            StartupLaunchTaskState state,
            StartupLaunchTaskState requestEnableState = StartupLaunchTaskState.Enabled,
            StartupLaunchTaskState? stateAfterRequest = null,
            StartupLaunchTaskState? stateAfterDisable = null)
        {
            State = state;
            _requestEnableState = requestEnableState;
            _stateAfterRequest = stateAfterRequest;
            _stateAfterDisable = stateAfterDisable;
        }

        public StartupLaunchTaskState State { get; private set; }

        public int RequestEnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public Task<StartupLaunchTaskState> RequestEnableAsync()
        {
            RequestEnableCount++;
            if (_stateAfterRequest is StartupLaunchTaskState state)
            {
                State = state;
            }

            return Task.FromResult(_requestEnableState);
        }

        public void Disable()
        {
            DisableCount++;
            if (_stateAfterDisable is StartupLaunchTaskState state)
            {
                State = state;
            }
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

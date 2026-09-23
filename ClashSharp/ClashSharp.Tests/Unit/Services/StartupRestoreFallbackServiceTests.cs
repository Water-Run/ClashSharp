using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Exercises the independent Windows-managed restore task through the shared verified startup boundary.</summary>
public sealed class StartupRestoreFallbackServiceTests
{
    /// <summary>Registration, status and removal must never touch the normal application startup task.</summary>
    [Fact]
    public async Task RegisterAndRemove_UseIndependentTaskAndVerifyWindowsState()
    {
        Provider provider = new();
        StartupRestoreFallbackService service = Create(provider);
        Assert.False((await service.GetStatusAsync(CancellationToken.None)).IsRegistered);
        await service.RegisterAsync(CancellationToken.None);
        Assert.True((await service.GetStatusAsync(CancellationToken.None)).IsRegistered);
        Assert.Contains(StartupRestoreFallbackService.HelperArgument,
            (await service.GetStatusAsync(CancellationToken.None)).CommandLine, StringComparison.Ordinal);
        await service.RegisterAsync(CancellationToken.None);
        Assert.Equal(1, provider.Startup.EnableCalls);
        await service.RemoveRegistrationAsync(CancellationToken.None);
        Assert.False((await service.GetStatusAsync(CancellationToken.None)).IsRegistered);
        Assert.Empty((await service.GetStatusAsync(CancellationToken.None)).CommandLine);
        Assert.Equal(1, provider.Startup.DisableCalls);
        Assert.All(provider.RequestedIds, id => Assert.Equal(StartupRestoreFallbackService.TaskId, id));
        Assert.DoesNotContain(StartupLaunchService.TaskId, provider.RequestedIds);
    }

    /// <summary>Windows denial and failed verification must not be turned into registered status.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Register_WhenNotEnabled_ReportsFailure(bool accepted, bool stateChanges)
    {
        Provider provider = new();
        provider.Startup.AcceptEnable = accepted;
        provider.Startup.ChangeEnableState = stateChanges;
        StartupRestoreFallbackService service = Create(provider);
        await Assert.ThrowsAsync<StartupLaunchUpdateException>(() => service.RegisterAsync(CancellationToken.None));
        Assert.False((await service.GetStatusAsync(CancellationToken.None)).IsRegistered);
    }

    /// <summary>A failed disable verification must retain the observed enabled state.</summary>
    [Fact]
    public async Task Remove_WhenWindowsRemainsEnabled_ReportsFailure()
    {
        Provider provider = new();
        StartupRestoreFallbackService service = Create(provider);
        await service.RegisterAsync(CancellationToken.None);
        provider.Startup.ChangeDisableState = false;
        await Assert.ThrowsAsync<StartupLaunchUpdateException>(() => service.RemoveRegistrationAsync(CancellationToken.None));
        Assert.True((await service.GetStatusAsync(CancellationToken.None)).IsRegistered);
    }

    /// <summary>An unreadable task must not be reported as an unregistered task.</summary>
    [Fact]
    public async Task Status_WhenWindowsUnavailable_ReportsFailure()
    {
        Provider provider = new() { Unavailable = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(provider).GetStatusAsync(CancellationToken.None));
    }

    /// <summary>Cancelled page operations must not change startup registration.</summary>
    [Fact]
    public async Task Register_WhenCancelled_DoesNotCallWindows()
    {
        Provider provider = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(provider).RegisterAsync(cancellation.Token));
        Assert.Empty(provider.RequestedIds);
    }

    private static StartupRestoreFallbackService Create(Provider provider) =>
        new(new StartupLaunchService(provider, new Log(), static key => key, StartupRestoreFallbackService.TaskId));

    private sealed class Provider : IStartupLaunchTaskProvider
    {
        public PlatformTask Startup { get; } = new();
        public List<string> RequestedIds { get; } = [];
        public bool Unavailable { get; init; }

        public Task<IStartupLaunchTask> GetAsync(string taskId)
        {
            RequestedIds.Add(taskId);
            if (Unavailable) { throw new InvalidOperationException("Windows task API unavailable"); }
            return Task.FromResult<IStartupLaunchTask>(Startup);
        }
    }

    private sealed class PlatformTask : IStartupLaunchTask
    {
        public StartupLaunchTaskState State { get; private set; } = StartupLaunchTaskState.Disabled;
        public bool AcceptEnable { get; set; } = true;
        public bool ChangeEnableState { get; set; } = true;
        public bool ChangeDisableState { get; set; } = true;
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }

        public Task<StartupLaunchTaskState> RequestEnableAsync()
        {
            EnableCalls++;
            if (AcceptEnable && ChangeEnableState) { State = StartupLaunchTaskState.Enabled; }
            return Task.FromResult(AcceptEnable ? StartupLaunchTaskState.Enabled : StartupLaunchTaskState.Disabled);
        }

        public void Disable()
        {
            DisableCalls++;
            if (ChangeDisableState) { State = StartupLaunchTaskState.Disabled; }
        }
    }

    private sealed class Log : IStartupLaunchLog
    {
        public void AppendLog(string level, string category, string message, string? detail) { }
    }
}

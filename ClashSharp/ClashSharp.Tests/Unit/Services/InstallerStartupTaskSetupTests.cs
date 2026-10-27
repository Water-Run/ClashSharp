extern alias ClashSharpUi;
using Windows.ApplicationModel;
using IInstallerStartupTask = ClashSharpUi::ClashSharp.Hosting.Startup.IInstallerStartupTask;
using InstallerStartupTaskSetup = ClashSharpUi::ClashSharp.Hosting.Startup.InstallerStartupTaskSetup;

namespace ClashSharp.Tests.Unit.Services;

public sealed class InstallerStartupTaskSetupTests
{
    [Theory]
    [InlineData(false, StartupTaskState.Disabled, true, 0, 0)]
    [InlineData(false, StartupTaskState.Enabled, true, 0, 1)]
    [InlineData(false, StartupTaskState.DisabledByUser, true, 0, 0)]
    [InlineData(false, StartupTaskState.DisabledByPolicy, true, 0, 0)]
    [InlineData(false, StartupTaskState.EnabledByPolicy, false, 0, 0)]
    [InlineData(true, StartupTaskState.Disabled, true, 1, 0)]
    [InlineData(true, StartupTaskState.Enabled, true, 0, 0)]
    [InlineData(true, StartupTaskState.DisabledByUser, false, 0, 0)]
    [InlineData(true, StartupTaskState.DisabledByPolicy, false, 0, 0)]
    [InlineData(true, StartupTaskState.EnabledByPolicy, true, 0, 0)]
    public async Task AppliesOnlyTheSelectedTransitionAndReportsPolicy(bool enabled, StartupTaskState initial,
        bool expected, int enables, int disables)
    {
        TaskStub task = new(initial);
        int reads = 0;
        bool result = await InstallerStartupTaskSetup.ApplyAsync(enabled, () =>
        { reads++; return Task.FromResult<IInstallerStartupTask>(task); }, CancellationToken.None);
        Assert.Equal(expected, result);
        Assert.Equal(enables, task.Enables);
        Assert.Equal(disables, task.Disables);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task DeniedEnableIsObservedAndCancellationDoesNotStartMutation()
    {
        TaskStub task = new(StartupTaskState.Disabled) { DenyEnable = true };
        Assert.False(await InstallerStartupTaskSetup.ApplyAsync(true,
            () => Task.FromResult<IInstallerStartupTask>(task), CancellationToken.None));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstallerStartupTaskSetup.ApplyAsync(true,
            () => throw new InvalidOperationException("Cancelled setup must not read Windows state."), cancelled.Token));
        Assert.Equal(1, task.Enables);
    }

    private sealed class TaskStub(StartupTaskState state) : IInstallerStartupTask
    {
        public StartupTaskState State { get; private set; } = state;
        internal int Enables { get; private set; }
        internal int Disables { get; private set; }
        internal bool DenyEnable { get; init; }
        public void Disable() { Disables++; State = StartupTaskState.Disabled; }
        public Task EnableAsync() { Enables++; if (!DenyEnable) { State = StartupTaskState.Enabled; } return Task.CompletedTask; }
    }
}

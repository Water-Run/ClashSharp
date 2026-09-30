using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FullReset_UsesReplacementPortAndReloadsOnlyAfterCompletion(bool restart, bool warning)
    {
        FakeSettingsStore store = new() { MixedPort = 23456 };
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SettingsViewModel page = CreateReplacementViewModel(store, async _ =>
        {
            entered.SetResult();
            await finish.Task;
            store.MixedPort = 10000;
            return new(restart, warning ? ["data.replacement.test_warning"] : []);
        });

        Task reset = page.ResetAllSettingsAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reset.IsCompleted);
            Assert.Equal(23456, page.MixedPort);
        }
        finally { finish.TrySetResult(); }
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000, page.MixedPort);
        Assert.Equal(restart, page.HasRestartRequiredSettings);
        Assert.Equal(warning && !restart, page.HasDataReplacementWarning);
        page.Load();
        Assert.Equal(restart, page.HasRestartRequiredSettings);
    }

    [Fact]
    public async Task FullReset_RecoveryFailurePreservesTypedFailureAndDoesNotIssueAnotherRestart()
    {
        FakeSettingsStore store = new() { MixedPort = 23456 };
        SettingsDataReplacementRecoveryException failure = new(false, [new IOException("native restoration failed")]);
        SettingsViewModel page = CreateReplacementViewModel(store, _ => throw failure);

        var actual = await Assert.ThrowsAsync<SettingsDataReplacementRecoveryException>(() => page.ResetAllSettingsAsync(CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.True(page.IsResetRecoveryRequired);
        Assert.True(page.HasRestartRequiredSettings);
        Assert.Equal("Settings.DataReplacement.RecoveryRequired.Description", page.OperationErrorText);
        Assert.Equal(23456, page.MixedPort);
    }

    [Fact]
    public async Task FullReset_PreCommitFailureReleasesPageGateForRetryWithoutClaimingSuccess()
    {
        FakeSettingsStore store = new() { MixedPort = 23456 };
        int attempts = 0;
        SettingsViewModel page = CreateReplacementViewModel(store, _ =>
        {
            if (++attempts == 1) { throw new IOException("candidate could not be opened"); }
            store.MixedPort = 10000;
            return Task.FromResult(new SettingsDataReplacementResult(false, []));
        });

        await Assert.ThrowsAsync<IOException>(() => page.ResetAllSettingsAsync(CancellationToken.None));
        Assert.False(page.IsResetRecoveryRequired);
        Assert.Equal(23456, page.MixedPort);
        await page.ResetAllSettingsAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
        Assert.Equal(10000, page.MixedPort);
    }

    [Fact]
    public async Task FullReset_CanceledCallerCannotAbandonPortCompletionAfterItsCommitPoint()
    {
        FakeSettingsStore store = new() { MixedPort = 23456 };
        using CancellationTokenSource cancellation = new();
        SettingsViewModel page = CreateReplacementViewModel(store, token =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            store.MixedPort = 10000;
            return Task.FromResult(new SettingsDataReplacementResult(false, []));
        });

        await page.ResetAllSettingsAsync(cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(10000, page.MixedPort);
    }

    private static SettingsViewModel CreateReplacementViewModel(FakeSettingsStore store,
        Func<CancellationToken, Task<SettingsDataReplacementResult>> replace) =>
        CreateMaintenanceViewModel(store, _ => throw new InvalidOperationException("Legacy appearance path used."),
            () => throw new InvalidOperationException("Legacy reset path used."), () => { },
            requestResetRecoveryRestart: () => throw new InvalidOperationException("Restart already belongs to the replacement port."),
            beginDestructiveRuntimeMutationAsync: _ => throw new InvalidOperationException("Legacy destructive scope used."),
            replaceAllSettingsAsync: replace);
}

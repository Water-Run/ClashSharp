using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting;

namespace ClashSharp.Tests.Unit.Startup;

public sealed class StartupInstanceArbitrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstLaunch_RegistersBeforeDiscoveringOtherRole(bool restore)
    {
        List<string> trace = [];
        PrimaryInstanceOwnership result = await StartupInstanceArbitration.AcquireAsync(
            restore,
            role => { trace.Add($"register:{role}"); return true; },
            role => { trace.Add($"find:{role}"); return null; },
            (_, _) => throw new InvalidOperationException("Unexpected wait"),
            _ => throw new InvalidOperationException("Unexpected redirect"),
            CancellationToken.None);

        Assert.Equal(PrimaryInstanceOwnership.Primary, result);
        Assert.Equal([$"register:{restore}", $"find:{!restore}"], trace);
    }

    [Fact]
    public async Task NormalLaunch_HelperStillRunning_DoesNotAdmitHostUntilHelperExits()
    {
        TaskCompletionSource helperExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool waiting = false;
        Task<PrimaryInstanceOwnership> admission = StartupInstanceArbitration.AcquireAsync(
            false,
            _ => true,
            restore => restore ? 42 : throw new InvalidOperationException("Wrong role"),
            (processId, _) => { Assert.Equal(42, processId); waiting = true; return helperExited.Task; },
            _ => throw new InvalidOperationException("Normal launch must not redirect to helper"),
            CancellationToken.None);

        Assert.True(waiting);
        Assert.False(admission.IsCompleted);
        helperExited.SetResult();
        Assert.Equal(PrimaryInstanceOwnership.Primary, await admission);
    }

    [Fact]
    public async Task HelperLaunch_NormalInstanceRegistered_ExitsWithoutActivatingOrConstructingHost()
    {
        PrimaryInstanceOwnership result = await StartupInstanceArbitration.AcquireAsync(
            true,
            _ => true,
            restore => !restore ? 42 : throw new InvalidOperationException("Wrong role"),
            (_, _) => throw new InvalidOperationException("Helper must not wait for GUI"),
            _ => throw new InvalidOperationException("Helper must not activate GUI"),
            CancellationToken.None);

        Assert.Equal(PrimaryInstanceOwnership.Redirected, result);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task DuplicateLaunch_OnlyNormalRoleRedirects(bool restore, int expectedRedirections)
    {
        int redirections = 0;
        PrimaryInstanceOwnership result = await StartupInstanceArbitration.AcquireAsync(
            restore,
            _ => false,
            _ => throw new InvalidOperationException("Duplicate must not discover or start a host"),
            (_, _) => throw new InvalidOperationException("Duplicate must not wait"),
            _ => { redirections++; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(PrimaryInstanceOwnership.Redirected, result);
        Assert.Equal(expectedRedirections, redirections);
    }

    [Fact]
    public async Task HelperWaitFails_NormalHostIsNeverAdmitted()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => StartupInstanceArbitration.AcquireAsync(
            false, _ => true, _ => 42,
            (_, _) => Task.FromException(new TimeoutException("Helper is still active")),
            _ => throw new InvalidOperationException("Unexpected redirect"),
            CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeAdmission_DoesNotRegister()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupInstanceArbitration.AcquireAsync(
            false,
            _ => throw new InvalidOperationException("Cancelled launch must not register"),
            _ => null, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            cancellation.Token));
    }
}

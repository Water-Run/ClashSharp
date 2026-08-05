extern alias RecoveryWatchdog;

using System.Globalization;
using RecoveryWatchdogInvocation = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogInvocation;
using RecoveryWatchdogLease = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogLease;
using RecoveryWatchdogLeaseFileStore = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogLeaseFileStore;
using RecoveryWatchdogRunner = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogRunner;

namespace ClashSharp.Tests.Unit.Services;

public sealed class RecoveryWatchdogRunnerTests
{
    [Fact]
    public async Task RunAsync_AfterAbruptParentExit_RestoresExactArmedLease()
    {
        RecoveryWatchdogInvocation invocation = CreateInvocation();
        RecoveryWatchdogLease? lease = invocation.ToLease();
        bool parentWaited = false;
        bool restored = false;
        RecoveryWatchdogRunner runner = new(
            (actual, _) =>
            {
                Assert.Equal(invocation, actual);
                parentWaited = true;
                return Task.CompletedTask;
            },
            _ => Task.FromResult<IDisposable?>(new TestLock()),
            () => lease,
            expected =>
            {
                Assert.Equal(invocation.ToLease(), expected);
                lease = null;
            },
            () =>
            {
                restored = true;
                return true;
            });

        int result = await runner.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.True(parentWaited);
        Assert.True(restored);
        Assert.Null(lease);
    }

    [Fact]
    public async Task RunAsync_WhenNormalExitDisarmsDuringWait_IsNoOp()
    {
        RecoveryWatchdogInvocation invocation = CreateInvocation();
        RecoveryWatchdogLease? lease = invocation.ToLease();
        bool restored = false;
        RecoveryWatchdogRunner runner = new(
            (_, _) =>
            {
                lease = null;
                return Task.CompletedTask;
            },
            _ => Task.FromResult<IDisposable?>(new TestLock()),
            () => lease,
            _ => throw new Xunit.Sdk.XunitException("A disarmed lease must not be cleared again."),
            () =>
            {
                restored = true;
                return true;
            });

        int result = await runner.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.False(restored);
    }

    [Fact]
    public async Task RunAsync_WhenNewInstanceReplacesLease_IsNoOp()
    {
        RecoveryWatchdogInvocation invocation = CreateInvocation();
        RecoveryWatchdogLease? lease = invocation.ToLease();
        RecoveryWatchdogRunner runner = new(
            (_, _) =>
            {
                lease = CreateInvocation().ToLease();
                return Task.CompletedTask;
            },
            _ => Task.FromResult<IDisposable?>(new TestLock()),
            () => lease,
            _ => throw new Xunit.Sdk.XunitException("A replacement lease must be preserved."),
            () => throw new Xunit.Sdk.XunitException("A stale helper must not restore."));

        int result = await runner.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.NotEqual(invocation.ToLease(), lease);
    }

    [Fact]
    public async Task RunAsync_WhenRecoveryLockUnavailable_LeavesFallbackJournalUntouched()
    {
        RecoveryWatchdogInvocation invocation = CreateInvocation();
        RecoveryWatchdogLease lease = invocation.ToLease();
        RecoveryWatchdogRunner runner = new(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<IDisposable?>(null),
            () => lease,
            _ => throw new Xunit.Sdk.XunitException("The lease must remain for next-start recovery."),
            () => throw new Xunit.Sdk.XunitException("Recovery without the per-user lock is forbidden."));

        int result = await runner.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public void InvocationParse_RequiresExactBoundedOptionSet()
    {
        RecoveryWatchdogInvocation expected = CreateInvocation();
        string[] arguments =
        [
            "--nonce", expected.Nonce.ToString("N"),
            "--parent-pid", expected.ParentProcessId.ToString(CultureInfo.InvariantCulture),
            "--parent-start-utc-ticks", expected.ParentStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
        ];

        Assert.Equal(expected, RecoveryWatchdogInvocation.Parse(arguments));
        Assert.Throws<ArgumentException>(() => RecoveryWatchdogInvocation.Parse(
            [.. arguments, "--extra", "value"]));
    }

    [Fact]
    public void LeaseFileStore_ClearIfMatches_PreservesReplacementLease()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashSharp.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "lease.json");
        try
        {
            RecoveryWatchdogLeaseFileStore store = new(path);
            RecoveryWatchdogLease stale = CreateInvocation().ToLease();
            RecoveryWatchdogLease replacement = CreateInvocation().ToLease();
            store.Write(replacement);

            store.ClearIfMatches(stale);

            Assert.Equal(replacement, store.Read());
            store.ClearIfMatches(replacement);
            Assert.Null(store.Read());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RecoveryWatchdogInvocation CreateInvocation()
    {
        return new RecoveryWatchdogInvocation(
            Guid.NewGuid(),
            Random.Shared.Next(1, int.MaxValue),
            DateTime.UtcNow.Ticks);
    }

    private sealed class TestLock : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

extern alias RecoveryWatchdog;

using System.Diagnostics;
using Invocation = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogInvocation;
using Lease = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogLease;
using LeaseStore = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogLeaseFileStore;
using RecoveryLock = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogFileLock;
using Runner = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogRunner;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Decision contract of the recovery watchdog: it restores only while the exact lease it was armed for
/// is still current at all three checkpoints and it holds the exclusive recovery lock; every other
/// path is a no-op, and a failing step never discards the ownership evidence.
/// </summary>
public sealed class WatchdogRunnerContractTests
{
    private enum LeaseView
    {
        Match,
        Absent,
        OtherNonce,
        OtherParentPid,
        OtherStartTicks,
    }

    private static Invocation NewInvocation(Random? random = null)
    {
        random ??= new Random(20261109);
        byte[] bytes = new byte[16];
        random.NextBytes(bytes);
        return new Invocation(new Guid(bytes), random.Next(1, int.MaxValue), 638_000_000_000_000_000L + random.Next(1, 1_000_000));
    }

    private static Lease? View(Invocation invocation, LeaseView view) => view switch
    {
        LeaseView.Match => invocation.ToLease(),
        LeaseView.Absent => null,
        LeaseView.OtherNonce => invocation.ToLease() with { Nonce = Guid.NewGuid() },
        LeaseView.OtherParentPid => invocation.ToLease() with { ParentProcessId = invocation.ParentProcessId == 1 ? 2 : 1 },
        _ => invocation.ToLease() with { ParentStartTimeUtcTicks = invocation.ParentStartTimeUtcTicks + 1 },
    };

    private sealed class Probe
    {
        internal List<string> Events { get; } = [];

        internal int LockDisposals { get; set; }

        internal int Restores { get; set; }

        internal List<Lease> Cleared { get; } = [];
    }

    [Fact]
    public async Task EveryCombinationOfLeaseViewsAndLockAvailability_RestoresIffTheExactLeaseIsCurrentThroughout()
    {
        Invocation invocation = NewInvocation();
        LeaseView[] views = Enum.GetValues<LeaseView>();
        int cases = 0;

        foreach (LeaseView first in views)
        {
            foreach (LeaseView second in views)
            {
                foreach (LeaseView third in views)
                {
                    foreach (bool lockAvailable in new[] { true, false })
                    {
                        foreach (bool restoreSucceeds in new[] { true, false })
                        {
                            cases++;
                            Probe probe = new();
                            LeaseView[] sequence = [first, second, third];
                            int reads = 0;
                            Runner runner = new(
                                (_, _) =>
                                {
                                    probe.Events.Add("wait");
                                    return Task.CompletedTask;
                                },
                                _ =>
                                {
                                    probe.Events.Add("lock");
                                    return Task.FromResult<IDisposable?>(lockAvailable ? new CountingLock(probe) : null);
                                },
                                () =>
                                {
                                    probe.Events.Add("read");
                                    return View(invocation, sequence[Math.Min(reads++, 2)]);
                                },
                                lease =>
                                {
                                    probe.Events.Add("clear");
                                    probe.Cleared.Add(lease);
                                },
                                () =>
                                {
                                    probe.Events.Add("restore");
                                    probe.Restores++;
                                    return restoreSucceeds;
                                });

                            int result = await runner.RunAsync(invocation, CancellationToken.None);

                            string label = $"{first}/{second}/{third} lock:{lockAvailable} restore:{restoreSucceeds}";
                            bool shouldRestore = first == LeaseView.Match
                                && second == LeaseView.Match
                                && third == LeaseView.Match
                                && lockAvailable;
                            if (shouldRestore)
                            {
                                Assert.Equal(
                                    ["read", "wait", "read", "lock", "read", "restore", "clear"],
                                    probe.Events);
                                Assert.Equal(restoreSucceeds ? 1 : 0, result);
                                Assert.Equal(invocation.ToLease(), Assert.Single(probe.Cleared));
                                Assert.Equal(1, probe.LockDisposals);
                            }
                            else
                            {
                                Assert.True(probe.Restores == 0, $"Restored without proof: {label}");
                                Assert.Empty(probe.Cleared);
                                Assert.Equal(0, result);
                                Assert.Equal(lockAvailable && probe.Events.Contains("lock") ? 1 : 0, probe.LockDisposals);
                                // Early exits must not touch later effect boundaries.
                                if (first != LeaseView.Match)
                                {
                                    Assert.Equal(["read"], probe.Events);
                                }
                                else if (second != LeaseView.Match)
                                {
                                    Assert.Equal(["read", "wait", "read"], probe.Events);
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(5 * 5 * 5 * 2 * 2, cases);
    }

    [Fact]
    public async Task RestoreFailure_PropagatesKeepsTheLeaseAndReleasesTheLock()
    {
        Invocation invocation = NewInvocation();
        Probe probe = new();
        IOException failure = new("registry unavailable");
        Runner runner = new(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<IDisposable?>(new CountingLock(probe)),
            invocation.ToLease,
            lease => probe.Cleared.Add(lease),
            () => throw failure);

        IOException thrown = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(invocation, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Empty(probe.Cleared);
        Assert.Equal(1, probe.LockDisposals);
    }

    [Fact]
    public async Task ClearFailure_AfterARestore_PropagatesWithoutRestoringTwiceAndReleasesTheLock()
    {
        Invocation invocation = NewInvocation();
        Probe probe = new();
        Runner runner = new(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<IDisposable?>(new CountingLock(probe)),
            invocation.ToLease,
            _ => throw new IOException("lease file locked"),
            () =>
            {
                probe.Restores++;
                return true;
            });

        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(invocation, CancellationToken.None));

        Assert.Equal(1, probe.Restores);
        Assert.Equal(1, probe.LockDisposals);
    }

    [Fact]
    public async Task WaitFailureOrCancellation_NeverReachesTheLockOrTheRestore()
    {
        Invocation invocation = NewInvocation();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Exception[] failures = [new InvalidOperationException("wait"), new OperationCanceledException(cancelled.Token)];

        foreach (Exception failure in failures)
        {
            Probe probe = new();
            Runner runner = new(
                (_, _) => Task.FromException(failure),
                _ =>
                {
                    probe.Events.Add("lock");
                    return Task.FromResult<IDisposable?>(new CountingLock(probe));
                },
                invocation.ToLease,
                lease => probe.Cleared.Add(lease),
                () =>
                {
                    probe.Restores++;
                    return true;
                });

            Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(invocation, cancelled.Token));

            Assert.Same(failure, thrown);
            Assert.Empty(probe.Events);
            Assert.Equal(0, probe.Restores);
            Assert.Empty(probe.Cleared);
        }
    }

    [Fact]
    public async Task LockAcquisitionFailure_Propagates_WithoutRestoring()
    {
        Invocation invocation = NewInvocation();
        Probe probe = new();
        Runner runner = new(
            (_, _) => Task.CompletedTask,
            _ => throw new UnauthorizedAccessException("lock denied"),
            invocation.ToLease,
            lease => probe.Cleared.Add(lease),
            () =>
            {
                probe.Restores++;
                return true;
            });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.RunAsync(invocation, CancellationToken.None));

        Assert.Equal(0, probe.Restores);
        Assert.Empty(probe.Cleared);
    }

    [Fact]
    public async Task ARepeatedRun_AfterASuccessfulRestore_IsANoOp_WithTheRealLeaseFileStore()
    {
        using TempDirectory directory = new();
        LeaseStore store = new(Path.Combine(directory.Path, "lease.json"));
        Invocation invocation = NewInvocation();
        store.Write(invocation.ToLease());
        int restores = 0;
        Runner runner = new(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<IDisposable?>(new CountingLock(new Probe())),
            store.Read,
            store.ClearIfMatches,
            () =>
            {
                restores++;
                return true;
            });

        Assert.Equal(1, await runner.RunAsync(invocation, CancellationToken.None));
        Assert.Equal(0, await runner.RunAsync(invocation, CancellationToken.None));
        Assert.Equal(0, await runner.RunAsync(invocation, CancellationToken.None));

        Assert.Equal(1, restores);
        Assert.Null(store.Read());
        Assert.False(File.Exists(Path.Combine(directory.Path, "lease.json")));
    }

    [Fact]
    public async Task ARestoreThatFails_LeavesTheLeaseSoALaterWatchdogCanRetry()
    {
        using TempDirectory directory = new();
        LeaseStore store = new(Path.Combine(directory.Path, "lease.json"));
        Invocation invocation = NewInvocation();
        store.Write(invocation.ToLease());
        bool fail = true;
        int restores = 0;
        Runner runner = new(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<IDisposable?>(new CountingLock(new Probe())),
            store.Read,
            store.ClearIfMatches,
            () =>
            {
                restores++;
                return fail ? throw new IOException("first attempt fails") : true;
            });

        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(invocation, CancellationToken.None));
        Assert.Equal(invocation.ToLease(), store.Read());
        fail = false;

        Assert.Equal(1, await runner.RunAsync(invocation, CancellationToken.None));

        Assert.Equal(2, restores);
        Assert.Null(store.Read());
    }

    [Fact]
    public async Task TwoWatchdogsForTheSameLease_NeverRestoreConcurrently_UnderTheRealExclusiveLock()
    {
        using TempDirectory directory = new();
        LeaseStore store = new(Path.Combine(directory.Path, "lease.json"));
        string lockPath = Path.Combine(directory.Path, "RecoveryWatchdog.lock");
        Invocation invocation = NewInvocation();
        store.Write(invocation.ToLease());
        TaskCompletionSource firstInRestore = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseFirst = new(false);
        int concurrent = 0;
        int maximumConcurrent = 0;
        int restores = 0;

        Runner Create(bool block) => new(
            (_, _) => Task.CompletedTask,
            async token => (IDisposable?)await RecoveryLock.TryAcquireAsync(lockPath, TimeSpan.Zero, token),
            store.Read,
            store.ClearIfMatches,
            () =>
            {
                int now = Interlocked.Increment(ref concurrent);
                maximumConcurrent = Math.Max(maximumConcurrent, now);
                restores++;
                if (block)
                {
                    firstInRestore.SetResult();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
                }

                Interlocked.Decrement(ref concurrent);
                return true;
            });

        Task<int> first = Task.Run(() => Create(block: true).RunAsync(invocation, CancellationToken.None));
        await firstInRestore.Task.WaitAsync(TestTimeouts.Completion);
        int second = await Create(block: false).RunAsync(invocation, CancellationToken.None);
        releaseFirst.Set();
        int firstResult = await first.WaitAsync(TestTimeouts.Completion);

        Assert.Equal(0, second);
        Assert.Equal(1, firstResult);
        Assert.Equal(1, restores);
        Assert.Equal(1, maximumConcurrent);
        Assert.Null(store.Read());
    }

    [Fact]
    public async Task WaitForParentExit_ReturnsImmediatelyWhenTheParentIdentityIsGoneOrReused()
    {
        using Process current = Process.GetCurrentProcess();
        long startTicks = current.StartTime.ToUniversalTime().Ticks;
        Invocation[] gone =
        [
            // Reused or recycled pid: same process, different creation time.
            new Invocation(Guid.NewGuid(), Environment.ProcessId, startTicks + 1),
            new Invocation(Guid.NewGuid(), Environment.ProcessId, 1),
            // A pid that cannot exist.
            new Invocation(Guid.NewGuid(), int.MaxValue, startTicks),
        ];

        foreach (Invocation invocation in gone)
        {
            await Runner.WaitForParentExitAsync(invocation, CancellationToken.None).WaitAsync(TestTimeouts.Completion);
        }
    }

    [Fact]
    public async Task WaitForParentExit_AttachesToTheExactParentAndObservesCancellation()
    {
        using Process current = Process.GetCurrentProcess();
        Invocation exact = new(Guid.NewGuid(), Environment.ProcessId, current.StartTime.ToUniversalTime().Ticks);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // The current process is alive, so a correct identity must wait (and then see the cancellation).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner.WaitForParentExitAsync(exact, cancellation.Token).WaitAsync(TestTimeouts.Completion));
    }

    private sealed class CountingLock(Probe probe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                probe.LockDisposals++;
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ClashSharp.Tests.Watchdog",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerParentEngineTests
{
    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task BuildsExactTrustedRequestAndDisposesBoundedSession(
        InstallerOperation operation)
    {
        using var fixture = Fixture();
        var session = new RecordingSession(static (_, _, _) => Task.FromResult(Success()));
        var factory = new RecordingSessionFactory(() => session);
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest,
            TargetSid,
            factory,
            new RecordingApplicationLock());
        var progress = new Progress<InstallerProgress>();

        InstallerExecutionResult result = await engine.ExecuteAsync(
            operation,
            progress,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, session.DisposeCount);
        InstallerRequest request = Assert.Single(session.Requests);
        Assert.Equal(operation, request.Operation);
        Assert.Equal(TargetSid, request.TargetSid);
        Assert.False(request.AllowReassociation);
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, request.ExpectedPackageVersion);
        Assert.Equal(fixture.Manifest.InstallerPayloadSha256, request.InstallerPayloadSha256);
        Assert.Same(progress, session.Progress);
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, engine.ReleaseVersion);
    }

    [Fact]
    public async Task ConcurrentCallIsRejectedBeforeSecondSessionCreation()
    {
        using var fixture = Fixture();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<InstallerExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RecordingSession((_, _, _) =>
        {
            started.SetResult();
            return completion.Task;
        });
        var factory = new RecordingSessionFactory(() => session);
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest,
            TargetSid,
            factory,
            new RecordingApplicationLock());

        Task<InstallerExecutionResult> first = engine.ExecuteAsync(
            InstallerOperation.Install,
            progress: null,
            CancellationToken.None);
        await started.Task;
        InstallerExecutionResult second = await engine.ExecuteAsync(
            InstallerOperation.Uninstall,
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, second.Outcome);
        Assert.Equal("installer.concurrent_action_rejected", second.DiagnosticCode);
        Assert.Equal(1, factory.CreateCount);
        completion.SetResult(Success());
        Assert.Equal(InstallerExecutionOutcome.Succeeded, (await first).Outcome);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task PreCancellationDoesNotCreateSessionAndReleasesSingleFlight()
    {
        using var fixture = Fixture();
        var factory = new RecordingSessionFactory(() =>
            new RecordingSession(static (_, _, _) => Task.FromResult(Success())));
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest,
            TargetSid,
            factory,
            new RecordingApplicationLock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(
            InstallerOperation.Install,
            progress: null,
            cancellation.Token));
        Assert.Equal(0, factory.CreateCount);

        InstallerExecutionResult retry = await engine.ExecuteAsync(
            InstallerOperation.Install,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, retry.Outcome);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task UnknownOperationAndMissingSessionFailClosed()
    {
        using var fixture = Fixture();
        var factory = new RecordingSessionFactory(static () => null!);
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest,
            TargetSid,
            factory,
            new RecordingApplicationLock());

        InstallerProtocolException operation = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => engine.ExecuteAsync(
                (InstallerOperation)999,
                progress: null,
                CancellationToken.None));
        Assert.Equal("installer.request.operation_invalid", operation.DiagnosticCode);
        Assert.Equal(0, factory.CreateCount);

        InstallerProtocolException session = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => engine.ExecuteAsync(
                InstallerOperation.Install,
                progress: null,
                CancellationToken.None));
        Assert.Equal("installer.runtime.execution_session_missing", session.DiagnosticCode);
    }

    [Fact]
    public async Task DisposePreventsNewSessionsWithoutAbortingAnAcceptedSession()
    {
        using var fixture = Fixture();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<InstallerExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RecordingSession((_, _, _) =>
        {
            started.SetResult();
            return completion.Task;
        });
        var factory = new RecordingSessionFactory(() => session);
        var engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest,
            TargetSid,
            factory,
            new RecordingApplicationLock());

        Task<InstallerExecutionResult> accepted = engine.ExecuteAsync(
            InstallerOperation.Install,
            progress: null,
            CancellationToken.None);
        await started.Task;
        engine.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ExecuteAsync(
            InstallerOperation.Uninstall,
            progress: null,
            CancellationToken.None));
        completion.SetResult(Success());
        Assert.Equal(InstallerExecutionOutcome.Succeeded, (await accepted).Outcome);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task ReadOnlyInspectionUsesExactIdentityWithoutCreatingExecutionSession()
    {
        using var fixture = Fixture();
        var factory = new RecordingSessionFactory(() =>
            new RecordingSession(static (_, _, _) => Task.FromResult(Success())));
        var expected = new InstallerRuntimeInspection(
            new InstallerEnvironmentSnapshot(
                IsSupported: true,
                InstalledPackageVersion: fixture.Manifest.ExpectedPackageVersion,
                IsApplicationRunning: false,
                BlockingDiagnosticCode: null),
            DurableTransaction: null,
            fixture.Manifest.ExpectedPackageVersion);
        var inspector = new RecordingInspector(expected);
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest,
            TargetSid,
            factory,
            new RecordingApplicationLock(),
            inspector);

        InstallerRuntimeInspection actual = await engine.InspectAsync(
            CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Equal(0, factory.CreateCount);
        InstallerRequest request = Assert.Single(inspector.Requests);
        Assert.Equal(InstallerOperation.Uninstall, request.Operation);
        Assert.Equal(TargetSid, request.TargetSid);
        Assert.False(request.AllowReassociation);
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, request.ExpectedPackageVersion);
        Assert.Equal(fixture.Manifest.InstallerPayloadSha256, request.InstallerPayloadSha256);
    }

    [Fact]
    public async Task InspectionCancellationAndConcurrentExecutionFailBeforeReadAuthority()
    {
        using var fixture = Fixture();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<InstallerExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RecordingSession((_, _, _) =>
        {
            started.SetResult();
            return completion.Task;
        });
        var inspector = new RecordingInspector(new InstallerRuntimeInspection(
            new InstallerEnvironmentSnapshot(true, null, false, null),
            null,
            fixture.Manifest.ExpectedPackageVersion));
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest,
            TargetSid,
            new RecordingSessionFactory(() => session),
            new RecordingApplicationLock(),
            inspector);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.InspectAsync(cancellation.Token));
        Assert.Empty(inspector.Requests);

        Task<InstallerExecutionResult> execution = engine.ExecuteAsync(
            InstallerOperation.Install,
            progress: null,
            CancellationToken.None);
        await started.Task;
        InstallerProtocolException concurrent = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => engine.InspectAsync(CancellationToken.None));
        Assert.Equal("installer.concurrent_action_rejected", concurrent.DiagnosticCode);
        Assert.Empty(inspector.Requests);
        completion.SetResult(Success());
        await execution;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockingInspectionReturnsToCallerAndRetainsLifetimeUntilWorkerDrains(bool disposeWhileActive)
    {
        using var fixture = Fixture();
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<Task<InstallerRuntimeInspection>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inspector = new BlockingInspector(entered, release);
        var factory = new RecordingSessionFactory(static () =>
            new RecordingSession(static (_, _, _) => Task.FromResult(Success())));
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest, TargetSid, factory, new RecordingApplicationLock(), inspector);
        int callerThread = 0;
        Task caller = Task.Factory.StartNew(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            returned.SetResult(engine.InspectAsync(cancellation.Token));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<InstallerRuntimeInspection>? inspection = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            inspection = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(callerThread, inspector.WorkerThread);
            Assert.False(inspection.IsCompleted);
            cancellation.Cancel();
            if (disposeWhileActive)
            {
                engine.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.InspectAsync(CancellationToken.None));
            }
            else
            {
                InstallerExecutionResult concurrent = await engine.ExecuteAsync(
                    InstallerOperation.Install, null, CancellationToken.None);
                Assert.Equal("installer.concurrent_action_rejected", concurrent.DiagnosticCode);
            }
            Assert.False(inspection.IsCompleted);
            Assert.Equal(0, factory.CreateCount);
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            await caller.WaitAsync(TimeSpan.FromSeconds(5));
            inspection ??= await returned.Task;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                inspection.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.True(inspector.Drained);
    }

    [Fact]
    public async Task InspectionRejectsMissingOrDifferentReleaseResult()
    {
        using var fixture = Fixture();
        var factory = new RecordingSessionFactory(() =>
            new RecordingSession(static (_, _, _) => Task.FromResult(Success())));
        var missing = new RecordingInspector(result: null);
        using WindowsInstallerParentEngine missingEngine =
            WindowsInstallerParentEngine.CreateForTesting(
                fixture.Manifest,
                TargetSid,
                factory,
                new RecordingApplicationLock(),
                missing);

        InstallerProtocolException missingResult = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => missingEngine.InspectAsync(
                CancellationToken.None));
        Assert.Equal(
            "installer.runtime.inspection_result_missing",
            missingResult.DiagnosticCode);

        var different = new RecordingInspector(new InstallerRuntimeInspection(
            new InstallerEnvironmentSnapshot(true, null, false, null),
            null,
            "9.9.9.9"));
        using WindowsInstallerParentEngine differentEngine =
            WindowsInstallerParentEngine.CreateForTesting(
                fixture.Manifest,
                TargetSid,
                factory,
                new RecordingApplicationLock(),
                different);
        InstallerProtocolException invalid = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => differentEngine.InspectAsync(
                CancellationToken.None));
        Assert.Equal(
            "installer.runtime.inspection_result_invalid",
            invalid.DiagnosticCode);
    }

    [Fact]
    public void DefaultCreationRejectsManifestBeforeReadingWindowsIdentity()
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsInstallerParentEngine.CreateDefault(
                "{}"u8.ToArray(),
                @"C:\Release\ClashSharp-Installer.exe"));

        Assert.Equal("installer.release.manifest_json_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public async Task AppLifetimeLeaseSurvivesExecutionAndSessionCleanup()
    {
        using var fixture = Fixture();
        var applicationLock = new RecordingApplicationLock();
        var session = new RecordingSession((_, _, _) =>
        {
            Assert.True(applicationLock.Held);
            return Task.FromResult(Success());
        })
        {
            OnDispose = () => Assert.True(applicationLock.Held),
        };
        var factory = new RecordingSessionFactory(() =>
        {
            Assert.True(applicationLock.Held);
            return session;
        });
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest, TargetSid, factory, applicationLock);

        await engine.ExecuteAsync(InstallerOperation.Repair, null, CancellationToken.None);

        Assert.False(applicationLock.Held);
        Assert.Equal(1, applicationLock.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSessionCreationOrDisposalReleasesAppLifetimeLease(bool failDisposal)
    {
        using var fixture = Fixture();
        var applicationLock = new RecordingApplicationLock();
        var expected = new IOException("injected");
        var session = new RecordingSession(static (_, _, _) => Task.FromResult(Success()))
        {
            OnDispose = () => throw expected,
        };
        var factory = new RecordingSessionFactory(() => failDisposal ? session : throw expected);
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest, TargetSid, factory, applicationLock);

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            engine.ExecuteAsync(InstallerOperation.Install, null, CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.False(applicationLock.Held);
        Assert.Equal(1, applicationLock.DisposeCount);
    }

    [Fact]
    public async Task BusyAppLifetimeLockRejectsExecutionBeforeSessionCreation()
    {
        using var fixture = Fixture();
        var applicationLock = new RecordingApplicationLock
        {
            AcquireFailure = new InstallerProtocolException("installer.application_running"),
        };
        var factory = new RecordingSessionFactory(() =>
            new RecordingSession(static (_, _, _) => Task.FromResult(Success())));
        using WindowsInstallerParentEngine engine = WindowsInstallerParentEngine.CreateForTesting(
            fixture.Manifest, TargetSid, factory, applicationLock);

        InstallerProtocolException actual = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            engine.ExecuteAsync(InstallerOperation.Install, null, CancellationToken.None));

        Assert.Equal("installer.application_running", actual.DiagnosticCode);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, applicationLock.DisposeCount);
    }

    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    private static WindowsPayloadFixture Fixture() => new(
        createPayload: false,
        removeCurrentUserCertificateOnDispose: false);

    private static InstallerExecutionResult Success() => new(
        InstallerExecutionOutcome.Succeeded,
        "installer.completed",
        InstallerTransactionPhase.Verified,
        RecoveryPending: false);

    private sealed class RecordingSessionFactory : IWindowsInstallerExecutionSessionFactory
    {
        private readonly Func<IWindowsInstallerExecutionSession> _create;

        internal RecordingSessionFactory(Func<IWindowsInstallerExecutionSession> create)
        {
            _create = create;
        }

        internal int CreateCount { get; private set; }

        public Task<IWindowsInstallerExecutionSession> CreateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            return Task.FromResult(_create());
        }
    }

    private sealed class RecordingSession : IWindowsInstallerExecutionSession
    {
        private readonly Func<
            InstallerRequest,
            IProgress<InstallerProgress>?,
            CancellationToken,
            Task<InstallerExecutionResult>> _execute;

        internal RecordingSession(
            Func<
                InstallerRequest,
                IProgress<InstallerProgress>?,
                CancellationToken,
                Task<InstallerExecutionResult>> execute)
        {
            _execute = execute;
        }

        internal List<InstallerRequest> Requests { get; } = [];

        internal IProgress<InstallerProgress>? Progress { get; private set; }

        internal int DisposeCount { get; private set; }

        internal Action? OnDispose { get; init; }

        public Task<InstallerExecutionResult> ExecuteAsync(
            InstallerRequest request,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken)
        {
            request.Validate();
            Requests.Add(request);
            Progress = progress;
            return _execute(request, progress, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            OnDispose?.Invoke();
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingApplicationLock : IWindowsInstallerApplicationLock, IDisposable
    {
        internal bool Held { get; private set; }

        internal int DisposeCount { get; private set; }

        internal Exception? AcquireFailure { get; init; }

        public IDisposable Acquire(string targetSid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TargetSid, targetSid);
            Assert.False(Held);
            if (AcquireFailure is not null)
            {
                throw AcquireFailure;
            }
            Held = true;
            return this;
        }

        public void Dispose()
        {
            Assert.True(Held);
            Held = false;
            DisposeCount++;
        }
    }

    private sealed class BlockingInspector(
        TaskCompletionSource entered,
        ManualResetEventSlim release) : IWindowsInstallerParentInspector
    {
        internal int WorkerThread { get; private set; }

        internal bool Drained { get; private set; }

        public Task<InstallerRuntimeInspection> InspectAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            request.Validate();
            WorkerThread = Environment.CurrentManagedThreadId;
            entered.SetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(15), CancellationToken.None))
                {
                    throw new TimeoutException("Blocking inspection fixture was not released.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Fixture must be cancelled before release.");
            }
            finally
            {
                Drained = true;
            }
        }
    }

    private sealed class RecordingInspector : IWindowsInstallerParentInspector
    {
        private readonly InstallerRuntimeInspection? _result;

        internal RecordingInspector(InstallerRuntimeInspection? result)
        {
            _result = result;
        }

        internal List<InstallerRequest> Requests { get; } = [];

        public Task<InstallerRuntimeInspection> InspectAsync(
            InstallerRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            Requests.Add(request);
            return Task.FromResult(_result!);
        }
    }
}

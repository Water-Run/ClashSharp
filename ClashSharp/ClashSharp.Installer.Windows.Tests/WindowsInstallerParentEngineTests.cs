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
            factory);
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
            factory);

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
            factory);
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
            factory);

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
            factory);

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
                @"C:\Release\ClashSharp.Installer.exe"));

        Assert.Equal("installer.release.manifest_json_invalid", exception.DiagnosticCode);
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
            DisposeCount++;
            return ValueTask.CompletedTask;
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

using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineHelperBrokerTests
{
    [Fact]
    public async Task OneVerifiedSelfHelperSessionCarriesMultipleTransactionCommands()
    {
        InstallerRequest request = Request();
        InstallerTransactionSnapshot prepared = Snapshot(
            request,
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand prepare = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        InstallerTransactionSnapshot reserved = prepare.GetExpectedSuccessfulState();
        InstallerMachineHelperCommand commitPackage = Command(
            InstallerMachineHelperVerb.CommitPackage,
            reserved);
        InstallerTransactionSnapshot package = commitPackage.GetExpectedSuccessfulState();
        using MemoryStream responses = await ResponsesAsync(
            InstallerMachineHelperResult.Succeeded(prepare, reserved),
            InstallerMachineHelperResult.Succeeded(commitPackage, package));
        using var requests = new MemoryStream();
        var serverFactory = new RecordingServerFactory(responses, requests);
        var process = new FakeElevatedProcess(processId: 4243);
        var launcher = new RecordingLauncher(process);
        var trust = new RecordingTrustVerifier();
        await using var broker = CreateBroker(trust, serverFactory, launcher);

        InstallerMachineHelperResult first = await broker.ExecuteAsync(prepare);
        InstallerMachineHelperResult second = await broker.ExecuteAsync(commitPackage);

        Assert.Equal(reserved, first.ValidateAgainst(prepare));
        Assert.Equal(package, second.ValidateAgainst(commitPackage));
        Assert.Equal(1, trust.CallCount);
        Assert.Equal(1, serverFactory.CreateCount);
        Assert.Equal(1, launcher.CallCount);
        Assert.Equal(4243, serverFactory.Server.VerifiedProcessId);
        Assert.Equal(4242, launcher.Bootstrap?.ParentProcessId);
        Assert.Equal("ClashSharp.Installer.exe", Path.GetFileName(trust.LastPath));
        requests.Position = 0;
        Assert.Equal(
            prepare,
            await InstallerMachineHelperFraming.ReadCommandAsync(
                requests,
                CancellationToken.None));
        Assert.Equal(
            commitPackage,
            await InstallerMachineHelperFraming.ReadCommandAsync(
                requests,
                CancellationToken.None));
        Assert.Equal(requests.Length, requests.Position);
    }

    [Fact]
    public async Task SuccessfulClearWaitsForHelperExitAndClosesTheBroker()
    {
        InstallerRequest request = Request();
        InstallerTransactionSnapshot verified = Snapshot(
            request,
            InstallerTransactionPhase.Verified);
        InstallerMachineHelperCommand clear = Command(
            InstallerMachineHelperVerb.Clear,
            verified);
        using MemoryStream responses = await ResponsesAsync(
            InstallerMachineHelperResult.Succeeded(clear, verified));
        using var requests = new MemoryStream();
        var serverFactory = new RecordingServerFactory(responses, requests);
        var process = new FakeElevatedProcess(processId: 4243, exitImmediately: true);
        var launcher = new RecordingLauncher(process);
        var trust = new RecordingTrustVerifier();
        await using var broker = CreateBroker(
            trust,
            serverFactory,
            launcher);

        InstallerMachineHelperResult result = await broker.ExecuteAsync(clear);

        Assert.Equal(verified, result.ValidateAgainst(clear));
        Assert.Equal(1, process.WaitCount);
        Assert.True(process.Disposed);
        Assert.True(serverFactory.Server.Disposed);
        Assert.True(trust.LastLease?.Disposed);
        InstallerProtocolException closed = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => broker.ExecuteAsync(clear));
        Assert.Equal("installer.machine_helper.session_completed", closed.DiagnosticCode);
    }

    [Fact]
    public async Task LostResultFaultsThePipeAndReportsUncertainState()
    {
        InstallerTransactionSnapshot prepared = Snapshot(
            Request(),
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        using var responses = new MemoryStream();
        using var requests = new MemoryStream();
        var serverFactory = new RecordingServerFactory(responses, requests);
        var process = new FakeElevatedProcess(processId: 4243);
        var trust = new RecordingTrustVerifier();
        await using var broker = CreateBroker(
            trust,
            serverFactory,
            new RecordingLauncher(process));

        InstallerStateUncertainException exception = await Assert.ThrowsAsync<
            InstallerStateUncertainException>(() => broker.ExecuteAsync(command));

        Assert.Equal("installer.machine_helper.response_unconfirmed", exception.DiagnosticCode);
        Assert.True(serverFactory.Server.Disposed);
        Assert.False(process.Disposed);
        Assert.False(trust.LastLease?.Disposed);
        InstallerStateUncertainException unusable = await Assert.ThrowsAsync<
            InstallerStateUncertainException>(() => broker.ExecuteAsync(command));
        Assert.Equal("installer.machine_helper.session_unusable", unusable.DiagnosticCode);
        process.SignalExit();
        await broker.DisposeAsync();
        Assert.True(process.Disposed);
        Assert.True(trust.LastLease?.Disposed);
    }

    [Fact]
    public async Task ConnectionTimeoutOccursAfterServerCreationButBeforeAnyCommandWrite()
    {
        InstallerTransactionSnapshot prepared = Snapshot(
            Request(),
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        using var responses = new MemoryStream();
        using var requests = new MemoryStream();
        var serverFactory = new RecordingServerFactory(
            responses,
            requests,
            static _ => throw new OperationCanceledException());
        var process = new FakeElevatedProcess(processId: 4243);
        await using var broker = CreateBroker(
            new RecordingTrustVerifier(),
            serverFactory,
            new RecordingLauncher(process));

        InstallerStateUncertainException exception = await Assert.ThrowsAsync<
            InstallerStateUncertainException>(() => broker.ExecuteAsync(command));

        Assert.Equal("installer.machine_helper.connection_timeout", exception.DiagnosticCode);
        Assert.Equal(0, requests.Length);
        Assert.True(serverFactory.Server.Disposed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task UacCancellationRemainsDistinctAndWritesNoCommand()
    {
        InstallerTransactionSnapshot prepared = Snapshot(
            Request(),
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        using var responses = new MemoryStream();
        using var requests = new MemoryStream();
        var serverFactory = new RecordingServerFactory(responses, requests);
        var launcher = new RecordingLauncher(
            static (_, _, _) => throw new InstallerUserCancelledException(
                "installer.elevation.user_cancelled"));
        await using var broker = CreateBroker(
            new RecordingTrustVerifier(),
            serverFactory,
            launcher);

        InstallerUserCancelledException exception = await Assert.ThrowsAsync<
            InstallerUserCancelledException>(() => broker.ExecuteAsync(command));

        Assert.Equal("installer.elevation.user_cancelled", exception.DiagnosticCode);
        Assert.Equal(0, requests.Length);
        Assert.True(serverFactory.Server.Disposed);
    }

    private static WindowsMachineHelperBroker CreateBroker(
        IWindowsInstallerExecutableTrustVerifier trustVerifier,
        IWindowsMachineHelperServerFactory serverFactory,
        IWindowsRunAsProcessLauncher launcher) =>
        new(
            Path.Combine(Path.GetTempPath(), "ClashSharp.Installer.exe"),
            trustVerifier,
            serverFactory,
            launcher,
            new WindowsMachineHelperBrokerLimits(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(50)),
            static () => 4242);

    private static async Task<MemoryStream> ResponsesAsync(
        params InstallerMachineHelperResult[] results)
    {
        var stream = new MemoryStream();
        foreach (InstallerMachineHelperResult result in results)
        {
            await InstallerMachineHelperFraming.WriteResultAsync(
                stream,
                result,
                CancellationToken.None);
        }

        stream.Position = 0;
        return stream;
    }

    private static InstallerMachineHelperCommand Command(
        InstallerMachineHelperVerb verb,
        InstallerTransactionSnapshot state) =>
        InstallerMachineHelperCommand.Create(
            InstallerMachineHelperInvocation.Create(verb, state),
            state);

    private static InstallerRequest Request() => new(
        InstallerOperation.Install,
        "S-1-5-21-100-200-300-1001",
        AllowReassociation: false,
        "1.2.3.4",
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");

    private static InstallerTransactionSnapshot Snapshot(
        InstallerRequest request,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        InstallerTransactionPhase[] order =
        [
            InstallerTransactionPhase.Prepared,
            InstallerTransactionPhase.MachineReserved,
            InstallerTransactionPhase.PackageCommitted,
            InstallerTransactionPhase.MachineCommitted,
            InstallerTransactionPhase.Verified,
        ];
        foreach (InstallerTransactionPhase next in order.Skip(1))
        {
            if (journal.Phase == phase)
            {
                break;
            }

            journal = journal.TransitionTo(next);
        }

        Assert.Equal(phase, journal.Phase);
        return InstallerTransactionSnapshot.Create(journal);
    }

    private sealed class RecordingTrustVerifier : IWindowsInstallerExecutableTrustVerifier
    {
        internal int CallCount { get; private set; }

        internal string? LastPath { get; private set; }

        internal RecordingTrustLease? LastLease { get; private set; }

        public Task<IWindowsInstallerExecutableTrustLease> VerifyAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastPath = executablePath;
            LastLease = new RecordingTrustLease(executablePath);
            return Task.FromResult<IWindowsInstallerExecutableTrustLease>(LastLease);
        }
    }

    private sealed class RecordingTrustLease : IWindowsInstallerExecutableTrustLease
    {
        internal RecordingTrustLease(string executablePath)
        {
            ExecutablePath = executablePath;
        }

        public string ExecutablePath { get; }

        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingServerFactory : IWindowsMachineHelperServerFactory
    {
        private readonly Stream _responses;
        private readonly Stream _requests;
        private readonly Func<CancellationToken, Task>? _wait;

        internal RecordingServerFactory(
            Stream responses,
            Stream requests,
            Func<CancellationToken, Task>? wait = null)
        {
            _responses = responses;
            _requests = requests;
            _wait = wait;
            Server = new RecordingServer(_responses, _requests, _wait);
        }

        internal int CreateCount { get; private set; }

        internal RecordingServer Server { get; }

        public IWindowsMachineHelperServer Create(InstallerMachineHelperBootstrap bootstrap)
        {
            bootstrap.Validate();
            CreateCount++;
            return Server;
        }
    }

    private sealed class RecordingServer : IWindowsMachineHelperServer
    {
        private readonly Func<CancellationToken, Task>? _wait;
        private readonly SplitDuplexStream _transport;

        internal RecordingServer(
            Stream responses,
            Stream requests,
            Func<CancellationToken, Task>? wait)
        {
            _wait = wait;
            _transport = new SplitDuplexStream(responses, requests);
        }

        public Stream Transport => _transport;

        internal int? VerifiedProcessId { get; private set; }

        internal bool Disposed { get; private set; }

        public Task WaitForConnectionAsync(CancellationToken cancellationToken) =>
            _wait?.Invoke(cancellationToken) ?? Task.CompletedTask;

        public void VerifyClient(int expectedHelperProcessId)
        {
            Assert.True(expectedHelperProcessId > 0);
            VerifiedProcessId = expectedHelperProcessId;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLauncher : IWindowsRunAsProcessLauncher
    {
        private readonly Func<
            string,
            InstallerMachineHelperBootstrap,
            CancellationToken,
            Task<IWindowsElevatedHelperProcess>> _start;

        internal RecordingLauncher(IWindowsElevatedHelperProcess process)
            : this((_, _, _) => Task.FromResult(process))
        {
        }

        internal RecordingLauncher(
            Func<
                string,
                InstallerMachineHelperBootstrap,
                CancellationToken,
                Task<IWindowsElevatedHelperProcess>> start)
        {
            _start = start;
        }

        internal int CallCount { get; private set; }

        internal InstallerMachineHelperBootstrap? Bootstrap { get; private set; }

        public Task<IWindowsElevatedHelperProcess> StartAsync(
            string executablePath,
            InstallerMachineHelperBootstrap bootstrap,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Bootstrap = bootstrap;
            return _start(executablePath, bootstrap, cancellationToken);
        }
    }

    private sealed class FakeElevatedProcess : IWindowsElevatedHelperProcess
    {
        private readonly TaskCompletionSource<bool> _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal FakeElevatedProcess(int processId, bool exitImmediately = false)
        {
            ProcessId = processId;
            if (exitImmediately)
            {
                _exit.TrySetResult(true);
            }
        }

        public int ProcessId { get; }

        public bool HasExited => _exit.Task.IsCompleted;

        internal int WaitCount { get; private set; }

        internal bool Disposed { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            return _exit.Task.WaitAsync(cancellationToken);
        }

        internal void SignalExit() => _exit.TrySetResult(true);

        public void Dispose() => Disposed = true;
    }

    private sealed class SplitDuplexStream : Stream
    {
        private readonly Stream _input;
        private readonly Stream _output;

        internal SplitDuplexStream(Stream input, Stream output)
        {
            _input = input;
            _output = output;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            _output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);
    }
}

using System.Text;
using System.Threading.Channels;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Retirement;

namespace ClashSharp.Installer.Windows.Tests;

public sealed partial class WindowsRetiredUninstallAuthorityTests
{
    [Fact]
    public async Task PreparationRefusalCrossesAuthenticatedPipeWithoutLosingItsDiagnostic()
    {
        using var fixture = new Fixture { Failure = "shared" };
        await using var pair = new SessionPair(fixture);
        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => pair.StartAsync());
        Assert.Equal("installer.retired_uninstall.injected_failure", error.DiagnosticCode);
        Assert.Null(pair.HostFailure);
        Assert.Null(fixture.Journal.Bytes);
        Assert.Empty(fixture.Active);
        Assert.True(pair.Exit.Task.IsCompleted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task DedicatedPipeCompletesNewAndEveryRecoveredPhaseWithOneAuthenticatedHelper(int recoveredPhase)
    {
        using var fixture = new Fixture();
        if (recoveredPhase >= 0) { fixture.Seed(recoveredPhase); }
        InstallerTransactionSnapshot? original = fixture.Journal.Bytes is { } bytes
            ? InstallerTransactionSnapshot.Create(InstallerTransactionCodec.Parse(bytes)) : null;
        await using var pair = new SessionPair(fixture);
        WindowsRetiredUninstallParentHandoff handoff = await pair.StartAsync();
        await using WindowsMachineHelperBroker broker = handoff.Broker;
        var receipts = new WindowsRetiredUninstallReceipts(handoff.Ready, broker);
        if (original is not null) { Assert.Equal(original, handoff.Ready); }
        fixture.RequireHeld();
        InstallerTransactionSnapshot state = handoff.Ready;
        while (state.Journal.Phase != InstallerTransactionPhase.Verified)
        {
            InstallerMachineHelperVerb verb = InstallerRetiredUninstallProtocol.FirstInvocation(state).Verb;
            if (verb == InstallerMachineHelperVerb.CommitPackage) { fixture.PackagePresent = false; }
            InstallerMachineHelperCommand command = Command(state, verb);
            InstallerMachineHelperResult result = await receipts.ExecuteAsync(command);
            Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
            state = result.ValidateAgainst(command);
            Assert.Equal(state, await receipts.LoadAsync(default));
        }
        if (recoveredPhase == 4)
        {
            await receipts.ExecuteAsync(Command(state, InstallerMachineHelperVerb.Verify));
        }
        await receipts.ApplyAsync(fixture.Request, new ReleaseLease(fixture), default);
        await receipts.ExecuteAsync(Command(state, InstallerMachineHelperVerb.Clear));
        Assert.Null(await receipts.LoadAsync(default));
        await pair.Exit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(pair.HostFailure);
        Assert.Equal(1, pair.Launches);
        Assert.Empty(fixture.Active);
        Assert.Null(fixture.Journal.Bytes);
        Assert.Null(fixture.Archive.Bytes);
        Assert.False(fixture.PackagePresent);
        Assert.False(fixture.CertificatePresent);
        Assert.Equal(recoveredPhase >= 3 ? 0 : 1, fixture.CertificateRemovals);
        string wire = Encoding.UTF8.GetString(pair.Helper.Written.ToArray());
        Assert.DoesNotContain("authenticationToken", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificateThumbprint", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profileRoot", wire, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("elevation")]
    [InlineData("helper-trust")]
    [InlineData("parent-acquire")]
    [InlineData("client-connect")]
    [InlineData("client-verify")]
    [InlineData("parent-alive")]
    [InlineData("server-verify")]
    [InlineData("parent-trust")]
    [InlineData("launch")]
    public async Task FailedEndpointAuthenticationCannotCreateRemovalState(string failure)
    {
        using var fixture = new Fixture();
        await using var pair = new SessionPair(fixture) { Failure = failure };
        await Assert.ThrowsAnyAsync<Exception>(() => pair.StartAsync());
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Active);
        Assert.Null(fixture.Journal.Bytes);
        Assert.Equal(0, fixture.CertificateRemovals);
    }

    [Fact]
    public async Task ClosedPipeDrainsAuthorityAndNextHelperResumesTheSamePrivateTransaction()
    {
        using var fixture = new Fixture();
        InstallerTransactionSnapshot interrupted;
        await using (var firstPair = new SessionPair(fixture))
        {
            WindowsRetiredUninstallParentHandoff first = await firstPair.StartAsync();
            await using (WindowsMachineHelperBroker broker = first.Broker)
            {
                var command = Command(first.Ready, InstallerMachineHelperVerb.Prepare);
                interrupted = (await broker.ExecuteAsync(command)).ValidateAgainst(command);
            }
            Assert.True(firstPair.Exit.Task.IsCompleted);
            Assert.Empty(fixture.Active);
        }
        await using var secondPair = new SessionPair(fixture);
        WindowsRetiredUninstallParentHandoff second = await secondPair.StartAsync();
        await using WindowsMachineHelperBroker resumed = second.Broker;
        Assert.Equal(interrupted, second.Ready);
        var remove = Command(second.Ready, InstallerMachineHelperVerb.Remove);
        var state = (await resumed.ExecuteAsync(remove)).ValidateAgainst(remove);
        Assert.Equal(InstallerTransactionPhase.MachineCommitted, state.Journal.Phase);
        Assert.Equal(0, fixture.CertificateRemovals);
    }

    private sealed class SessionPair : IWindowsRetiredUninstallServerFactory, IWindowsRetiredUninstallClientFactory,
        IWindowsRetiredUninstallProcessLauncher, IWindowsMachineHelperServerFactory, IWindowsRunAsProcessLauncher, IAsyncDisposable
    {
        internal Fixture Fixture { get; }
        internal PipeStream Parent { get; }
        internal PipeStream Helper { get; }
        internal List<string> Events { get; } = [];
        internal string? Failure { get; init; }
        internal Exception? HostFailure { get; private set; }
        internal TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Launches { get; private set; }
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _host;
        private InstallerRetiredUninstallBootstrap? _bootstrap;
        private readonly WindowsRetiredUninstallBroker _broker;

        internal SessionPair(Fixture fixture)
        {
            Fixture = fixture;
            var toParent = Channel.CreateUnbounded<byte[]>();
            var toHelper = Channel.CreateUnbounded<byte[]>();
            Parent = new(toParent.Reader, toHelper.Writer);
            Helper = new(toHelper.Reader, toParent.Writer);
            var limits = new WindowsMachineHelperBrokerLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
            _broker = new(@"C:\fixture\ClashSharp-Installer.exe", fixture.Payload.Manifest,
                WindowsOwnerTransferAccessFixture.PreviousSid, new PairTrust(this, "parent"), this, this,
                () => new WindowsMachineHelperBroker(@"C:\fixture\ClashSharp-Installer.exe", new PairTrust(this, "unused"),
                    this, this, limits, () => 4242), () => 4242, limits);
        }

        internal Task<WindowsRetiredUninstallParentHandoff> StartAsync(CancellationToken cancellationToken = default) =>
            _broker.StartAsync(cancellationToken);

        internal void Hit(string value)
        {
            lock (Events) { Events.Add(value); }
            if (Failure == value)
            {
                throw new InstallerProtocolException("installer.owner_transfer.injected_refusal");
            }
        }

        IWindowsMachineHelperServer IWindowsRetiredUninstallServerFactory.Create(InstallerRetiredUninstallBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
            return new PairServer(this);
        }
        IWindowsMachineHelperClient IWindowsRetiredUninstallClientFactory.Create(InstallerRetiredUninstallBootstrap bootstrap)
        {
            Assert.Equal(_bootstrap, bootstrap);
            return new PairClient(this);
        }
        public Task<IWindowsElevatedHelperProcess> StartAsync(string executablePath, InstallerRetiredUninstallBootstrap bootstrap, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Hit("launch");
            Launches++;
            var host = new WindowsRetiredUninstallHost(executablePath, Fixture.Payload.Manifest,
                new PairElevation(this), new PairTrust(this, "helper"), new PairParent(this), this, Fixture.Factory,
                new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
            _host = RunAsync(host, bootstrap);
            return Task.FromResult<IWindowsElevatedHelperProcess>(new PairProcess(this));
        }
        private async Task RunAsync(WindowsRetiredUninstallHost host, InstallerRetiredUninstallBootstrap bootstrap)
        {
            try { await host.RunAsync(bootstrap, default); }
            catch (Exception exception) { HostFailure = exception; }
            finally { Exit.TrySetResult(); }
        }
        IWindowsMachineHelperServer IWindowsMachineHelperServerFactory.Create(InstallerMachineHelperBootstrap bootstrap) =>
            throw new InvalidOperationException("An adopted session must not open another ordinary pipe.");
        Task<IWindowsElevatedHelperProcess> IWindowsRunAsProcessLauncher.StartAsync(string executablePath,
            InstallerMachineHelperBootstrap bootstrap, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An adopted session must not elevate again.");
        public async ValueTask DisposeAsync()
        {
            await Parent.DisposeAsync();
            await Helper.DisposeAsync();
            if (_host is not null) { await _host; }
            Parent.Written.Dispose();
            Helper.Written.Dispose();
        }

        private sealed class PairServer(SessionPair pair) : IWindowsMachineHelperServer
        {
            public Stream Transport => pair.Parent;
            public async Task WaitForConnectionAsync(CancellationToken cancellationToken)
            {
                Task first = await Task.WhenAny(pair._connected.Task, pair.Exit.Task).WaitAsync(cancellationToken);
                if (first == pair.Exit.Task && !pair._connected.Task.IsCompleted)
                {
                    throw new IOException("The isolated helper exited before connecting.");
                }
                await pair._connected.Task;
            }
            public void VerifyClient(int expectedHelperProcessId) { Assert.Equal(8888, expectedHelperProcessId); pair.Hit("server-verify"); }
            public ValueTask DisposeAsync() => pair.Parent.DisposeAsync();
        }
        private sealed class PairClient(SessionPair pair) : IWindowsMachineHelperClient
        {
            public Stream Transport => pair.Helper;
            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pair.Hit("client-connect");
                pair._connected.TrySetResult();
                return Task.CompletedTask;
            }
            public void VerifyServer(int expectedParentProcessId) { Assert.Equal(4242, expectedParentProcessId); pair.Hit("client-verify"); }
            public ValueTask DisposeAsync() => pair.Helper.DisposeAsync();
        }
        private sealed class PairProcess(SessionPair pair) : IWindowsElevatedHelperProcess
        {
            public int ProcessId => 8888;
            public bool HasExited => pair.Exit.Task.IsCompleted;
            public Task WaitForExitAsync(CancellationToken cancellationToken) => pair.Exit.Task.WaitAsync(cancellationToken);
            public void Dispose() { Assert.True(HasExited); pair.Hit("process-dispose"); }
        }
        private sealed class PairElevation(SessionPair pair) : IWindowsMachineHelperElevationVerifier
        {
            public void VerifyElevated() => pair.Hit("elevation");
        }
        private sealed class PairTrust(SessionPair pair, string side) : IWindowsInstallerExecutableTrustVerifier
        {
            public Task<IWindowsInstallerExecutableTrustLease> VerifyAsync(string executablePath, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pair.Hit(side + "-trust");
                return Task.FromResult<IWindowsInstallerExecutableTrustLease>(new PairTrustLease(pair, side, executablePath));
            }
        }
        private sealed class PairTrustLease(SessionPair pair, string side, string path) : IWindowsInstallerExecutableTrustLease
        {
            public string ExecutablePath => path;
            public void Dispose() => pair.Hit(side + "-trust-dispose");
        }
        private sealed class PairParent(SessionPair pair) : IWindowsMachineHelperParentProcessVerifier
        {
            public IWindowsMachineHelperParentProcessLease Acquire(int expectedParentProcessId, string expectedExecutablePath)
            {
                pair.Hit("parent-acquire");
                Assert.Equal(4242, expectedParentProcessId);
                Assert.Equal(@"C:\fixture\ClashSharp-Installer.exe", expectedExecutablePath);
                return new PairParentLease(pair);
            }
        }
        private sealed class PairParentLease(SessionPair pair) : IWindowsMachineHelperParentProcessLease
        {
            public int ProcessId => 4242;
            public string UserSid => WindowsOwnerTransferAccessFixture.PreviousSid;
            public void VerifyAlive() => pair.Hit("parent-alive");
            public void Dispose() => pair.Hit("parent-dispose");
        }
    }

    private sealed class PipeStream(ChannelReader<byte[]> input, ChannelWriter<byte[]> output) : Stream
    {
        private byte[] _pending = [];
        private int _offset;
        internal MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _pending.Length)
            {
                try { _pending = await input.ReadAsync(cancellationToken); _offset = 0; }
                catch (ChannelClosedException) { return 0; }
            }
            int count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] bytes = buffer.ToArray();
            await output.WriteAsync(bytes, cancellationToken);
            Written.Write(bytes);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        protected override void Dispose(bool disposing) { if (disposing) { output.TryComplete(); } base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

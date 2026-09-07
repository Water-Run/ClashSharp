using System.Text;
using System.Threading.Channels;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed partial class WindowsOwnerTransferAuthorityTests
{
    [Fact]
    public async Task DedicatedPipeTransfersThroughRealAuthorityAndCompletesOrdinaryCommandsWithoutRelaunch()
    {
        using var fixture = new Fixture();
        await using var pair = new SessionPair(fixture);
        WindowsOwnerTransferParentHandoff handoff = await pair.StartAsync((_, _) =>
        {
            Assert.Empty(fixture.Active);
            Assert.Null(fixture.Persistence.Bytes);
            Assert.Empty(fixture.Inner.Mutations);
            return Task.FromResult(true);
        });
        await using WindowsMachineHelperBroker broker = handoff.Broker;
        fixture.RequireHeld();
        InstallerTransactionSnapshot state = handoff.Continuation;
        foreach (InstallerMachineHelperVerb verb in new[] { InstallerMachineHelperVerb.Prepare,
            InstallerMachineHelperVerb.CommitPackage, InstallerMachineHelperVerb.Apply, InstallerMachineHelperVerb.Verify,
            InstallerMachineHelperVerb.Clear })
        {
            var command = InstallerMachineHelperCommand.Create(InstallerMachineHelperInvocation.Create(verb, state), state);
            InstallerMachineHelperResult result = await broker.ExecuteAsync(command);
            Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
            state = result.ValidateAgainst(command);
        }
        await pair.Exit.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(pair.HostFailure);
        Assert.Equal(1, pair.Launches);
        Assert.Empty(fixture.Active);
        Assert.Null(fixture.Persistence.Bytes);
        Assert.False(fixture.Inner.Native.Entries.ContainsKey(WindowsOwnerTransferAccessFixture.ContinuationPath));
        Assert.Equal(0, fixture.Inner.Native.LiveLeases);
        string wire = Encoding.UTF8.GetString(pair.Helper.Written.ToArray());
        Assert.DoesNotContain("authenticationToken", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profileRoot", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Inner.Journal.PreviousOwner.Association.AuthenticationToken, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Inner.Journal.NextOwner.Association.AuthenticationToken, wire, StringComparison.Ordinal);
        Assert.Equal(5, fixture.Events.Count(value => value.StartsWith("normal:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DecliningTheOfferLeavesNoPrivateOrOrdinaryJournalAndNoMutation()
    {
        using var fixture = new Fixture();
        await using var pair = new SessionPair(fixture);

        var error = await Assert.ThrowsAsync<InstallerUserCancelledException>(() => pair.StartAsync((offer, _) =>
        {
            Assert.False(offer.IsRecovery);
            Assert.Empty(fixture.Active);
            return Task.FromResult(false);
        }));

        Assert.Equal("installer.owner_transfer.declined", error.DiagnosticCode);
        Assert.Null(pair.HostFailure);
        Assert.Empty(fixture.Inner.Mutations);
        Assert.Null(fixture.Persistence.Bytes);
        Assert.Empty(fixture.Active);
        Assert.DoesNotContain("transfer-open", fixture.Events);
        Assert.True(pair.Exit.Task.IsCompleted);
        Assert.Equal(2, pair.Events.Count(value => value.EndsWith("trust-dispose", StringComparison.Ordinal)));
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
    public async Task EitherEndpointAuthenticationFailureCannotInspectOrAcquireTransferAuthority(string failure)
    {
        using var fixture = new Fixture();
        await using var pair = new SessionPair(fixture) { Failure = failure };
        bool displayed = false;

        await Assert.ThrowsAnyAsync<Exception>(() => pair.StartAsync((_, _) =>
        {
            displayed = true;
            return Task.FromResult(true);
        }));

        Assert.False(displayed);
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Inner.Mutations);
        Assert.Null(fixture.Persistence.Bytes);
        Assert.Empty(fixture.Active);
    }

    [Fact]
    public async Task ConfirmationGapStateChangeIsRejectedByTheRealAuthorityBeforeAnyPhase()
    {
        using var fixture = new Fixture();
        await using var pair = new SessionPair(fixture);

        await Assert.ThrowsAnyAsync<Exception>(() => pair.StartAsync((_, _) =>
        {
            var changed = fixture.Inner.Journal with
            {
                Continuation = fixture.Inner.Journal.Continuation with { TransactionId = new string('f', 64) },
            };
            fixture.Persistence.Bytes = InstallerOwnerTransferCodec.Serialize(changed);
            return Task.FromResult(true);
        }));

        Assert.Equal("installer.owner_transfer.confirmation_state_changed",
            Assert.IsType<InstallerProtocolException>(pair.HostFailure).DiagnosticCode);
        Assert.Empty(fixture.Inner.Mutations);
        Assert.DoesNotContain(fixture.Events, value => value.StartsWith("phase:", StringComparison.Ordinal));
        Assert.Empty(fixture.Active);
    }

    [Fact]
    public async Task ParentCancellationClosesThePipeButDrainsHelperMutationBeforeReleasingPins()
    {
        using var fixture = new Fixture();
        await using var pair = new SessionPair(fixture);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforePhase = (phase, _) =>
        {
            if (phase != InstallerOwnerTransferPhase.MachineAccessTransferred)
            {
                return Task.CompletedTask;
            }
            entered.TrySetResult();
            return drain.Task;
        };
        Task pending = pair.StartAsync((_, _) => Task.FromResult(true), cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.False(pair.Exit.Task.IsCompleted);
            fixture.RequireHeld();
            Assert.DoesNotContain("parent-trust-dispose", pair.Events);
        }
        finally
        {
            drain.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.True(pair.Exit.Task.IsCompleted);
        Assert.Empty(fixture.Active);
        Assert.Equal(InstallerTransactionSnapshot.Create(fixture.Inner.Journal.Continuation),
            await fixture.Inner.Ordinary.LoadAsync(default));
        Assert.Null(fixture.Persistence.Bytes);
        Assert.Contains("parent-trust-dispose", pair.Events);
    }

    [Fact]
    public async Task RecoveryOfferRetainsDurableInstallWhenCurrentPackageSuggestsRepair()
    {
        using var fixture = new Fixture();
        fixture.Persistence.Bytes = InstallerOwnerTransferCodec.Serialize(fixture.Inner.Journal);
        fixture.Confirmed = new(fixture.Inner.Journal, InstallerOwnerTransferSnapshot.Create(fixture.Inner.Journal));
        await using var pair = new SessionPair(fixture);
        InstallerOperation? offered = null;

        WindowsOwnerTransferParentHandoff handoff = await pair.StartAsync((offer, _) =>
        {
            Assert.True(offer.IsRecovery);
            offered = offer.Request.Operation;
            return Task.FromResult(true);
        }, operation: InstallerOperation.Repair);
        await handoff.Broker.DisposeAsync();
        await pair.Exit.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(InstallerOperation.Install, offered);
        Assert.Equal(InstallerOperation.Install, handoff.Continuation.Journal.Operation);
        Assert.DoesNotContain("preparation", fixture.Events);
        Assert.Empty(fixture.Active);
    }

    /// <summary>Actual codecs, host, source, transfer phases and ordinary broker; only Windows side effects and trust probes are fake.</summary>
    private sealed class SessionPair : IWindowsOwnerTransferServerFactory, IWindowsOwnerTransferClientFactory,
        IWindowsOwnerTransferProcessLauncher, IWindowsMachineHelperServerFactory, IWindowsRunAsProcessLauncher, IAsyncDisposable
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
        private InstallerOwnerTransferBootstrap? _bootstrap;
        private readonly WindowsOwnerTransferBroker _broker;

        internal SessionPair(Fixture fixture)
        {
            Fixture = fixture;
            var toParent = Channel.CreateUnbounded<byte[]>();
            var toHelper = Channel.CreateUnbounded<byte[]>();
            Parent = new(toParent.Reader, toHelper.Writer);
            Helper = new(toHelper.Reader, toParent.Writer);
            var limits = new WindowsMachineHelperBrokerLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
            _broker = new(@"C:\fixture\ClashSharp-Installer.exe", fixture.Inner.State.Machine.Release.Manifest,
                WindowsOwnerTransferAccessFixture.NextSid, new PairTrust(this, "parent"), this, this,
                () => new WindowsMachineHelperBroker(@"C:\fixture\ClashSharp-Installer.exe", new PairTrust(this, "unused"),
                    this, this, limits, () => 4242), () => 4242, limits);
        }

        internal Task<WindowsOwnerTransferParentHandoff> StartAsync(
            Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm,
            InstallerOperation operation = InstallerOperation.Install, CancellationToken cancellationToken = default) =>
            _broker.StartAsync(operation, confirm, cancellationToken);

        internal void Hit(string value)
        {
            lock (Events) { Events.Add(value); }
            if (Failure == value)
            {
                throw new InstallerProtocolException("installer.owner_transfer.injected_refusal");
            }
        }

        IWindowsMachineHelperServer IWindowsOwnerTransferServerFactory.Create(InstallerOwnerTransferBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
            return new PairServer(this);
        }
        IWindowsMachineHelperClient IWindowsOwnerTransferClientFactory.Create(InstallerOwnerTransferBootstrap bootstrap)
        {
            Assert.Equal(_bootstrap, bootstrap);
            return new PairClient(this);
        }
        public Task<IWindowsElevatedHelperProcess> StartAsync(string executablePath, InstallerOwnerTransferBootstrap bootstrap, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Hit("launch");
            Launches++;
            var source = new WindowsOwnerTransferOfferSource(new GlobalLock(Fixture), new ReleaseVerifier(Fixture), Fixture.Inner.Backend,
                () => new PairInspection(Fixture));
            var host = new WindowsOwnerTransferHost(executablePath, new PairElevation(this), new PairTrust(this, "helper"),
                new PairParent(this), this, source, Fixture.Factory,
                new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
            _host = RunAsync(host, bootstrap);
            return Task.FromResult<IWindowsElevatedHelperProcess>(new PairProcess(this));
        }
        private async Task RunAsync(WindowsOwnerTransferHost host, InstallerOwnerTransferBootstrap bootstrap)
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
            public string UserSid => WindowsOwnerTransferAccessFixture.NextSid;
            public void VerifyAlive() => pair.Hit("parent-alive");
            public void Dispose() => pair.Hit("parent-dispose");
        }
        private sealed class PairInspection(Fixture fixture) : IWindowsOwnerTransferInspection
        {
            public Task<InstallerOwnerTransferSnapshot?> LoadPrivateAsync(CancellationToken cancellationToken)
            {
                Assert.Equal(["global", "release"], fixture.Active);
                return Task.FromResult(fixture.Persistence.Bytes is { } bytes
                    ? InstallerOwnerTransferSnapshot.Create(InstallerOwnerTransferCodec.Parse(bytes)) : null);
            }
            public Task<InstallerOwnerTransferJournal> CaptureNewAsync(InstallerRequest request,
                ClashSharp.Installer.Payloads.InstallerReleaseManifest manifest, CancellationToken cancellationToken) =>
                Task.FromResult(fixture.Inner.Journal);
            public void Dispose() { }
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

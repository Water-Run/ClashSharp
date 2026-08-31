using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineHelperHostTests
{
    [Fact]
    public async Task AuthenticatesBothProcessesBeforeCreatingAuthorityAndClearsJournal()
    {
        var events = new List<string>();
        InstallerTransactionSnapshot verified = VerifiedSnapshot();
        InstallerMachineHelperCommand clear = InstallerMachineHelperCommand.Create(
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Clear,
                verified),
            verified);
        using var commands = new MemoryStream();
        await InstallerMachineHelperFraming.WriteCommandAsync(
            commands,
            clear,
            CancellationToken.None);
        commands.Position = 0;
        using var results = new MemoryStream();
        var store = new MemoryTransactionStore(verified);
        var operations = new RecordingOperations(events);
        var resourcesFactory = new RecordingAuthorityResourcesFactory(
            events,
            store,
            operations);
        var authorityFactory = new WindowsMachineHelperAuthorityFactory(resourcesFactory);
        var trust = new RecordingTrustVerifier(events);
        var parent = new RecordingParentVerifier(events);
        var clientFactory = new RecordingClientFactory(events, commands, results);
        var host = CreateHost(
            events,
            trust,
            parent,
            clientFactory,
            authorityFactory);
        InstallerMachineHelperBootstrap bootstrap = InstallerMachineHelperBootstrap.Create(
            clear.ToInvocation(),
            parentProcessId: 4242);

        await host.RunAsync(bootstrap, CancellationToken.None);

        results.Position = 0;
        InstallerMachineHelperResult result = await InstallerMachineHelperFraming
            .ReadResultAsync(results, CancellationToken.None);
        Assert.Equal(verified, result.ValidateAgainst(clear));
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.Equal(
            [
                "elevation",
                "trust",
                "parent-acquire",
                "client-create",
                "client-connect",
                "client-verify-server",
                "parent-alive",
                "authority-resources-create",
                "operation",
                "authority-resources-dispose",
                "client-dispose",
                "parent-dispose",
                "trust-dispose",
            ],
            events);
        Assert.Equal(1, operations.CallCount);
        Assert.Equal(4242, clientFactory.Client.VerifiedParentProcessId);
        Assert.Equal("S-1-5-21-100-200-300-1001", resourcesFactory.TargetSid);
    }

    [Fact]
    public async Task UnelevatedProcessFailsBeforeTrustOrPipeAccess()
    {
        var events = new List<string>();
        InstallerTransactionSnapshot verified = VerifiedSnapshot();
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Clear,
                verified);
        var elevation = new RecordingElevationVerifier(
            events,
            new InstallerProtocolException(
                "installer.machine_helper.elevation_required"));
        var host = new WindowsMachineHelperHost(
            ExecutablePath(),
            elevation,
            new RecordingTrustVerifier(events),
            new RecordingParentVerifier(events),
            new RecordingClientFactory(
                events,
                new MemoryStream(),
                new MemoryStream()),
            new RecordingAuthorityFactory(
                events,
                new MemoryTransactionStore(verified),
                new RecordingOperations(events)),
            Limits());

        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => host.RunAsync(
                InstallerMachineHelperBootstrap.Create(invocation, 4242),
                CancellationToken.None));

        Assert.Equal("installer.machine_helper.elevation_required", exception.DiagnosticCode);
        Assert.Equal(["elevation"], events);
    }

    [Fact]
    public async Task FirstCommandBootstrapMismatchFailsBeforeAuthorityCreation()
    {
        var events = new List<string>();
        InstallerTransactionSnapshot verified = VerifiedSnapshot();
        InstallerMachineHelperCommand clear = InstallerMachineHelperCommand.Create(
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Clear,
                verified),
            verified);
        using var commands = new MemoryStream();
        await InstallerMachineHelperFraming.WriteCommandAsync(
            commands,
            clear,
            CancellationToken.None);
        commands.Position = 0;
        using var results = new MemoryStream();
        var operations = new RecordingOperations(events);
        var authorityFactory = new RecordingAuthorityFactory(
            events,
            new MemoryTransactionStore(verified),
            operations);
        var host = CreateHost(
            events,
            new RecordingTrustVerifier(events),
            new RecordingParentVerifier(events),
            new RecordingClientFactory(events, commands, results),
            authorityFactory);
        InstallerMachineHelperInvocation mismatchedBootstrap = clear.ToInvocation() with
        {
            Verb = InstallerMachineHelperVerb.Verify,
        };

        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => host.RunAsync(
                InstallerMachineHelperBootstrap.Create(mismatchedBootstrap, 4242),
                CancellationToken.None));

        Assert.Equal(
            "installer.machine_helper.session_bootstrap_mismatch",
            exception.DiagnosticCode);
        Assert.DoesNotContain("authority-create", events);
        Assert.DoesNotContain("operation", events);
        Assert.Null(authorityFactory.TargetSid);
        Assert.Equal(0, results.Length);
        Assert.Equal(
            ["client-dispose", "parent-dispose", "trust-dispose"],
            events.TakeLast(3));
    }

    [Fact]
    public async Task FirstCommandTargetSidMismatchFailsBeforeAuthorityCreation()
    {
        var events = new List<string>();
        InstallerTransactionSnapshot verified = VerifiedSnapshot(
            "S-1-5-21-100-200-300-2002");
        InstallerMachineHelperCommand clear = InstallerMachineHelperCommand.Create(
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Clear,
                verified),
            verified);
        using var commands = new MemoryStream();
        await InstallerMachineHelperFraming.WriteCommandAsync(
            commands,
            clear,
            CancellationToken.None);
        commands.Position = 0;
        using var results = new MemoryStream();
        var operations = new RecordingOperations(events);
        var authorityFactory = new RecordingAuthorityFactory(
            events,
            new MemoryTransactionStore(verified),
            operations);
        var host = CreateHost(
            events,
            new RecordingTrustVerifier(events),
            new RecordingParentVerifier(events),
            new RecordingClientFactory(events, commands, results),
            authorityFactory);

        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => host.RunAsync(
                InstallerMachineHelperBootstrap.Create(clear.ToInvocation(), 4242),
                CancellationToken.None));

        Assert.Equal(
            "installer.machine_helper.target_sid_mismatch",
            exception.DiagnosticCode);
        Assert.DoesNotContain("authority-create", events);
        Assert.DoesNotContain("operation", events);
        Assert.Null(authorityFactory.TargetSid);
        Assert.Equal(0, results.Length);
        Assert.Equal(
            ["client-dispose", "parent-dispose", "trust-dispose"],
            events.TakeLast(3));
    }

    [Fact]
    public async Task AuthorityInitializationFailureDisposesTargetBoundResources()
    {
        var events = new List<string>();
        InstallerTransactionSnapshot requested = VerifiedSnapshot();
        InstallerTransactionSnapshot conflicting = VerifiedSnapshot();
        var resourcesFactory = new RecordingAuthorityResourcesFactory(
            events,
            new MemoryTransactionStore(conflicting),
            new RecordingOperations(events));
        var authorityFactory = new WindowsMachineHelperAuthorityFactory(resourcesFactory);

        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => authorityFactory.CreateAsync(
                InstallerMachineHelperInvocation.Create(
                    InstallerMachineHelperVerb.Clear,
                    requested),
                requested.Journal.TargetSid,
                CancellationToken.None));

        Assert.Equal(
            "installer.machine_helper.session_transaction_mismatch",
            exception.DiagnosticCode);
        Assert.Equal(
            ["authority-resources-create", "authority-resources-dispose"],
            events);
        Assert.Equal(requested.Journal.TargetSid, resourcesFactory.TargetSid);
    }

    [Fact]
    public async Task PeerVerificationFailureDisposesAllAcquiredHandlesBeforeAuthority()
    {
        var events = new List<string>();
        InstallerTransactionSnapshot verified = VerifiedSnapshot();
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Clear,
                verified);
        var clientFactory = new RecordingClientFactory(
            events,
            new MemoryStream(),
            new MemoryStream(),
            verifyFailure: new InstallerProtocolException(
                "installer.machine_helper.pipe_peer_identity_invalid"));
        var host = CreateHost(
            events,
            new RecordingTrustVerifier(events),
            new RecordingParentVerifier(events),
            clientFactory,
            new RecordingAuthorityFactory(
                events,
                new MemoryTransactionStore(verified),
                new RecordingOperations(events)));

        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => host.RunAsync(
                InstallerMachineHelperBootstrap.Create(invocation, 4242),
                CancellationToken.None));

        Assert.Equal(
            "installer.machine_helper.pipe_peer_identity_invalid",
            exception.DiagnosticCode);
        Assert.DoesNotContain("authority-create", events);
        Assert.Equal(
            ["client-dispose", "parent-dispose", "trust-dispose"],
            events.TakeLast(3));
    }

    private static WindowsMachineHelperHost CreateHost(
        List<string> events,
        IWindowsInstallerExecutableTrustVerifier trust,
        IWindowsMachineHelperParentProcessVerifier parent,
        IWindowsMachineHelperClientFactory client,
        IWindowsMachineHelperAuthorityFactory authority) =>
        new(
            ExecutablePath(),
            new RecordingElevationVerifier(events),
            trust,
            parent,
            client,
            authority,
            Limits());

    private static WindowsMachineHelperHostLimits Limits() => new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2));

    private static string ExecutablePath() =>
        Path.Combine(Path.GetTempPath(), "ClashSharp.Installer.exe");

    private static InstallerTransactionSnapshot VerifiedSnapshot(
        string targetSid = "S-1-5-21-100-200-300-1001")
    {
        var request = new InstallerRequest(
            InstallerOperation.Install,
            targetSid,
            AllowReassociation: false,
            "1.2.3.4",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        foreach (InstallerTransactionPhase phase in new[]
        {
            InstallerTransactionPhase.MachineReserved,
            InstallerTransactionPhase.PackageCommitted,
            InstallerTransactionPhase.MachineCommitted,
            InstallerTransactionPhase.Verified,
        })
        {
            journal = journal.TransitionTo(phase);
        }

        return InstallerTransactionSnapshot.Create(journal);
    }

    private sealed class RecordingElevationVerifier : IWindowsMachineHelperElevationVerifier
    {
        private readonly List<string> _events;
        private readonly Exception? _failure;

        internal RecordingElevationVerifier(List<string> events, Exception? failure = null)
        {
            _events = events;
            _failure = failure;
        }

        public void VerifyElevated()
        {
            _events.Add("elevation");
            if (_failure is not null)
            {
                throw _failure;
            }
        }
    }

    private sealed class RecordingTrustVerifier : IWindowsInstallerExecutableTrustVerifier
    {
        private readonly List<string> _events;

        internal RecordingTrustVerifier(List<string> events)
        {
            _events = events;
        }

        public Task<IWindowsInstallerExecutableTrustLease> VerifyAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("trust");
            return Task.FromResult<IWindowsInstallerExecutableTrustLease>(
                new RecordingTrustLease(_events, executablePath));
        }
    }

    private sealed class RecordingTrustLease : IWindowsInstallerExecutableTrustLease
    {
        private readonly List<string> _events;

        internal RecordingTrustLease(List<string> events, string executablePath)
        {
            _events = events;
            ExecutablePath = executablePath;
        }

        public string ExecutablePath { get; }

        public void Dispose() => _events.Add("trust-dispose");
    }

    private sealed class RecordingParentVerifier : IWindowsMachineHelperParentProcessVerifier
    {
        private readonly List<string> _events;

        internal RecordingParentVerifier(List<string> events)
        {
            _events = events;
        }

        public IWindowsMachineHelperParentProcessLease Acquire(
            int expectedParentProcessId,
            string expectedExecutablePath)
        {
            _events.Add("parent-acquire");
            Assert.Equal(ExecutablePath(), expectedExecutablePath);
            return new RecordingParentLease(_events, expectedParentProcessId);
        }
    }

    private sealed class RecordingParentLease : IWindowsMachineHelperParentProcessLease
    {
        private readonly List<string> _events;

        internal RecordingParentLease(List<string> events, int processId)
        {
            _events = events;
            ProcessId = processId;
        }

        public int ProcessId { get; }

        public string UserSid => "S-1-5-21-100-200-300-1001";

        public void VerifyAlive() => _events.Add("parent-alive");

        public void Dispose() => _events.Add("parent-dispose");
    }

    private sealed class RecordingClientFactory : IWindowsMachineHelperClientFactory
    {
        private readonly List<string> _events;
        private readonly Stream _commands;
        private readonly Stream _results;
        private readonly Exception? _verifyFailure;

        internal RecordingClientFactory(
            List<string> events,
            Stream commands,
            Stream results,
            Exception? verifyFailure = null)
        {
            _events = events;
            _commands = commands;
            _results = results;
            _verifyFailure = verifyFailure;
            Client = new RecordingClient(
                _events,
                new SplitDuplexStream(_commands, _results),
                _verifyFailure);
        }

        internal RecordingClient Client { get; }

        public IWindowsMachineHelperClient Create(InstallerMachineHelperBootstrap bootstrap)
        {
            bootstrap.Validate();
            _events.Add("client-create");
            return Client;
        }
    }

    private sealed class RecordingClient : IWindowsMachineHelperClient
    {
        private readonly List<string> _events;
        private readonly Exception? _verifyFailure;

        internal RecordingClient(
            List<string> events,
            Stream transport,
            Exception? verifyFailure)
        {
            _events = events;
            Transport = transport;
            _verifyFailure = verifyFailure;
        }

        public Stream Transport { get; }

        internal int? VerifiedParentProcessId { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("client-connect");
            return Task.CompletedTask;
        }

        public void VerifyServer(int expectedParentProcessId)
        {
            _events.Add("client-verify-server");
            VerifiedParentProcessId = expectedParentProcessId;
            if (_verifyFailure is not null)
            {
                throw _verifyFailure;
            }
        }

        public ValueTask DisposeAsync()
        {
            _events.Add("client-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAuthorityFactory : IWindowsMachineHelperAuthorityFactory
    {
        private readonly List<string> _events;
        private readonly IInstallerTransactionStore _store;
        private readonly IInstallerMachineHelperOperationExecutor _operations;

        internal string? TargetSid { get; private set; }

        internal RecordingAuthorityFactory(
            List<string> events,
            IInstallerTransactionStore store,
            IInstallerMachineHelperOperationExecutor operations)
        {
            _events = events;
            _store = store;
            _operations = operations;
        }

        public async Task<IWindowsMachineHelperAuthorityLease> CreateAsync(
            InstallerMachineHelperInvocation bootstrap,
            string targetSid,
            CancellationToken cancellationToken)
        {
            _events.Add("authority-create");
            TargetSid = targetSid;
            InstallerMachineHelperAuthoritySession session = await
                InstallerMachineHelperAuthoritySession.CreateAsync(
                    bootstrap,
                    targetSid,
                    _store,
                    _operations,
                    cancellationToken);
            return new RecordingAuthorityLease(session);
        }
    }

    private sealed class RecordingAuthorityLease : IWindowsMachineHelperAuthorityLease
    {
        internal RecordingAuthorityLease(InstallerMachineHelperAuthoritySession session)
        {
            Session = session;
        }

        public InstallerMachineHelperAuthoritySession Session { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingAuthorityResourcesFactory
        : IWindowsMachineHelperAuthorityResourcesFactory
    {
        private readonly List<string> _events;
        private readonly IInstallerTransactionStore _store;
        private readonly IInstallerMachineHelperOperationExecutor _operations;

        internal RecordingAuthorityResourcesFactory(
            List<string> events,
            IInstallerTransactionStore store,
            IInstallerMachineHelperOperationExecutor operations)
        {
            _events = events;
            _store = store;
            _operations = operations;
        }

        internal string? TargetSid { get; private set; }

        public IWindowsMachineHelperAuthorityResources Create(string targetSid)
        {
            _events.Add("authority-resources-create");
            TargetSid = targetSid;
            return new RecordingAuthorityResources(_events, _store, _operations);
        }
    }

    private sealed class RecordingAuthorityResources
        : IWindowsMachineHelperAuthorityResources
    {
        private readonly List<string> _events;

        internal RecordingAuthorityResources(
            List<string> events,
            IInstallerTransactionStore transactionStore,
            IInstallerMachineHelperOperationExecutor operations)
        {
            _events = events;
            TransactionStore = transactionStore;
            Operations = operations;
        }

        public IInstallerTransactionStore TransactionStore { get; }

        public IInstallerMachineHelperOperationExecutor Operations { get; }

        public ValueTask DisposeAsync()
        {
            _events.Add("authority-resources-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOperations : IInstallerMachineHelperOperationExecutor
    {
        private readonly List<string> _events;

        internal RecordingOperations(List<string> events)
        {
            _events = events;
        }

        internal int CallCount { get; private set; }

        public Task ExecuteAsync(
            InstallerMachineHelperCommand command,
            InstallerMachineHelperSessionDisposition disposition,
            CancellationToken cancellationToken)
        {
            command.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("operation");
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryTransactionStore : IInstallerTransactionStore
    {
        private InstallerTransactionSnapshot? _state;

        internal MemoryTransactionStore(InstallerTransactionSnapshot? state)
        {
            _state = state;
        }

        public Task<InstallerTransactionSnapshot?> LoadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_state);
        }

        public Task<InstallerTransactionSnapshot> SaveAsync(
            InstallerTransactionJournal journal,
            string? expectedCurrentHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? actualHash = _state?.ContentHash;
            if (!string.Equals(actualHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                throw new InstallerProtocolException("installer.test.cas_mismatch");
            }

            _state = InstallerTransactionSnapshot.Create(journal);
            return Task.FromResult(_state);
        }

        public Task ClearVerifiedAsync(
            string transactionId,
            string expectedCurrentHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state is null
                || _state.Journal.Phase != InstallerTransactionPhase.Verified
                || !string.Equals(
                    _state.Journal.TransactionId,
                    transactionId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    _state.ContentHash,
                    expectedCurrentHash,
                    StringComparison.Ordinal))
            {
                throw new InstallerProtocolException("installer.test.clear_mismatch");
            }

            _state = null;
            return Task.CompletedTask;
        }
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

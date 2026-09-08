using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsServiceReadinessVerifierTests
{
    [Fact]
    public async Task ExactServiceAnswersCorrelatedHelloAndConnectionIsDrained()
    {
        using var fixture = new Fixture();

        await fixture.Verifier().VerifyAsync(fixture.Plan, CancellationToken.None);

        MihomoServiceIpcRequest request = Assert.Single(fixture.Connection.Requests);
        Assert.Equal(MihomoServiceIpcCommand.Hello, request.Command);
        Assert.Equal(fixture.Plan.Association.AuthenticationToken, request.AuthenticationToken);
        Assert.Null(request.Generation);
        Assert.Equal(fixture.Plan.Association.BuildServicePipeName(), fixture.Factory.PipeName);
        Assert.Equal(3, fixture.Native.Calls);
        Assert.True(fixture.Connection.Disposed);
    }

    [Theory]
    [InlineData(0u, 42u)]
    [InlineData(41u, 42u)]
    [InlineData(42u, 0u)]
    public async Task UnauthenticatedServerReceivesNoCredential(uint pipeProcessId, uint serviceProcessId)
    {
        using var fixture = new Fixture();
        fixture.Connection.ServerProcessId = pipeProcessId;
        fixture.Native.Snapshot = fixture.Native.Snapshot with { ProcessId = serviceProcessId };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Verifier().VerifyAsync(fixture.Plan, CancellationToken.None));

        Assert.Equal("installer.machine.service_pipe_identity_mismatch", failure.DiagnosticCode);
        Assert.Empty(fixture.Connection.Requests);
        Assert.True(fixture.Connection.Disposed);
    }

    [Fact]
    public async Task ServiceExitingAfterHelloCannotCommitVerification()
    {
        using var fixture = new Fixture();
        fixture.Connection.Respond = request =>
        {
            fixture.Native.Snapshot = fixture.Native.Snapshot with
            {
                RuntimeState = WindowsServiceRuntimeState.Stopped,
                ProcessId = 0,
            };
            return fixture.Response(request);
        };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Verifier().VerifyAsync(fixture.Plan, CancellationToken.None));

        Assert.Equal("installer.machine.service_postcondition_failed", failure.DiagnosticCode);
        Assert.True(fixture.Connection.Disposed);
    }

    [Theory]
    [InlineData("correlation")]
    [InlineData("protocol")]
    [InlineData("failure")]
    [InlineData("version")]
    [InlineData("child")]
    public async Task WrongOrUnreadyHelloCannotCommitVerification(string drift)
    {
        using var fixture = new Fixture();
        fixture.Connection.Respond = request =>
        {
            MihomoServiceIpcResponse response = fixture.Response(request);
            return drift switch
            {
                "correlation" => response with { RequestId = Guid.NewGuid() },
                "protocol" => response with { ProtocolVersion = request.ProtocolVersion + 1 },
                "failure" => response with { Succeeded = false, ErrorCode = "service.ipc.authentication_failed" },
                "version" => response with { Snapshot = response.Snapshot! with { ServiceVersion = "0.9.0.0" } },
                "child" => response with { Snapshot = response.Snapshot! with { ChildState = MihomoServiceChildState.Starting } },
                _ => throw new InvalidOperationException(),
            };
        };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Verifier().VerifyAsync(fixture.Plan, CancellationToken.None));

        Assert.Equal("installer.machine.service_readiness_failed", failure.DiagnosticCode);
        Assert.True(fixture.Connection.Disposed);
    }

    [Fact]
    public async Task ServiceProcessReplacementAfterHelloCannotCommitVerification()
    {
        using var fixture = new Fixture();
        fixture.Connection.Respond = request =>
        {
            fixture.Native.Snapshot = fixture.Native.Snapshot with { ProcessId = 43 };
            return fixture.Response(request);
        };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Verifier().VerifyAsync(fixture.Plan, CancellationToken.None));

        Assert.Equal("installer.machine.service_pipe_identity_mismatch", failure.DiagnosticCode);
        Assert.True(fixture.Connection.Disposed);
    }

    [Fact]
    public async Task DeadlineCancelsAndDrainsAnUnresponsiveService()
    {
        using var fixture = new Fixture();
        fixture.Connection.Hang = true;

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Verifier(TimeSpan.FromMilliseconds(30)).VerifyAsync(fixture.Plan, CancellationToken.None));

        Assert.Equal("installer.machine.service_readiness_timeout", failure.DiagnosticCode);
        Assert.True(fixture.Connection.Disposed);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAndDrainsTheConnection()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Connection.Hang = true;
        Task verification = fixture.Verifier().VerifyAsync(fixture.Plan, cancellation.Token);
        await fixture.Connection.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
        Assert.True(fixture.Connection.Disposed);
    }

    [Fact]
    public async Task PreCancellationDoesNotInspectOrConnect()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Verifier().VerifyAsync(fixture.Plan, cancellation.Token));

        Assert.Equal(0, fixture.Native.Calls);
        Assert.Null(fixture.Factory.PipeName);
    }

    private sealed class Fixture : IDisposable
    {
        private const string TargetSid = "S-1-5-21-100-200-300-1001";
        private readonly WindowsPayloadFixture _payload = new(
            createPayload: false, removeCurrentUserCertificateOnDispose: false);

        internal Fixture()
        {
            Plan = WindowsMachineDeploymentPlan.Create(
                _payload.Request(targetSid: TargetSid), _payload.Manifest,
                InstallerMachineAssociation.Create(TargetSid, new string('a', 64)),
                @"C:\Program Files", @"C:\ProgramData", @"C:\Users\owner");
            Native = new Native(new WindowsServiceSnapshot(Plan.Service,
                WindowsServiceRuntimeState.Running,
                WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid), ProcessId: 42));
            Connection = new Connection { Respond = Response };
            Factory = new ConnectionFactory(Connection);
        }

        internal WindowsMachineDeploymentPlan Plan { get; }
        internal Native Native { get; }
        internal Connection Connection { get; }
        internal ConnectionFactory Factory { get; }
        internal WindowsServiceReadinessVerifier Verifier(TimeSpan? timeout = null) =>
            new(Native, Factory, timeout ?? TimeSpan.FromSeconds(10));

        internal MihomoServiceIpcResponse Response(MihomoServiceIpcRequest request) => new()
        {
            ProtocolVersion = request.ProtocolVersion,
            RequestId = request.RequestId,
            Succeeded = true,
            Snapshot = new MihomoServiceIpcSnapshot
            {
                SessionId = Guid.NewGuid(),
                ServiceVersion = Plan.Request.ExpectedPackageVersion,
                ChildState = MihomoServiceChildState.Stopped,
            },
        };

        public void Dispose() => _payload.Dispose();
    }

    private sealed class Native(WindowsServiceSnapshot snapshot) : IWindowsServiceConfigurationNative
    {
        internal WindowsServiceSnapshot Snapshot { get; set; } = snapshot;
        internal int Calls { get; private set; }

        public WindowsServiceSnapshot? Inspect(string serviceName)
        {
            Assert.Equal(WindowsMachineDeploymentPlan.ServiceName, serviceName);
            Calls++;
            return Snapshot;
        }
    }

    private sealed class ConnectionFactory(Connection connection) : IWindowsServiceReadinessConnectionFactory
    {
        internal string? PipeName { get; private set; }

        public Task<IWindowsServiceReadinessConnection> ConnectAsync(string pipeName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PipeName = pipeName;
            return Task.FromResult<IWindowsServiceReadinessConnection>(connection);
        }
    }

    private sealed class Connection : IWindowsServiceReadinessConnection
    {
        public uint ServerProcessId { get; set; } = 42;
        internal List<MihomoServiceIpcRequest> Requests { get; } = [];
        internal Func<MihomoServiceIpcRequest, MihomoServiceIpcResponse> Respond { get; set; } = null!;
        internal bool Hang { get; set; }
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MihomoServiceIpcResponse> ExchangeAsync(MihomoServiceIpcRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Entered.SetResult();
            if (Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Respond(request);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

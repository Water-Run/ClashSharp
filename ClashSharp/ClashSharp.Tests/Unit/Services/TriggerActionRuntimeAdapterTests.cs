extern alias ClashSharpUi;

using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using ClashSharp.ServiceProtocol;
using AppSettingsService = ClashSharpUi::ClashSharp.Service.AppSettingsService;
using ConnectionSamplingService = ClashSharpUi::ClashSharp.Service.ConnectionSamplingService;
using IIdempotentTriggerNotificationSink =
    ClashSharpUi::ClashSharp.Service.IIdempotentTriggerNotificationSink;
using IMihomoControllerServiceBroker =
    ClashSharpUi::ClashSharp.Service.IMihomoControllerServiceBroker;
using MihomoConnectionService = ClashSharpUi::ClashSharp.Service.MihomoConnectionService;
using MihomoControllerClient = ClashSharpUi::ClashSharp.Service.MihomoControllerClient;
using MihomoServiceStatus = ClashSharpUi::ClashSharp.Model.MihomoServiceStatus;
using StartupLaunchService = ClashSharpUi::ClashSharp.Service.StartupLaunchService;
using TriggerActionRuntimeAdapter =
    ClashSharpUi::ClashSharp.Service.TriggerActionRuntimeAdapter;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Guards owner-aware close-connections trigger behavior.</summary>
public sealed class TriggerActionRuntimeAdapterTests
{
    private const string ActiveConfigurationHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly Guid ServiceSessionId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task ProbeCloseConnections_ServiceOwnerWithActiveConnection_ReturnsNotDesired()
    {
        FakeServiceBroker broker = new(ServiceOwnedStatus())
        {
            Connections = [Connection("connection-1")],
        };
        TriggerActionRuntimeAdapter runtime = CreateRuntime(broker);

        TriggerActionProbeResult result = await runtime.ProbeAsync(
            CloseConnectionsAction(),
            CancellationToken.None);

        Assert.Equal(TriggerActionProbeStatus.NotDesired, result.Status);
        Assert.Equal([MihomoServiceIpcCommand.GetConnections], broker.Commands);
    }

    [Fact]
    public async Task ProbeCloseConnections_ServiceOwnerWithoutConnections_ReturnsDesired()
    {
        FakeServiceBroker broker = new(ServiceOwnedStatus());
        TriggerActionRuntimeAdapter runtime = CreateRuntime(broker);

        TriggerActionProbeResult result = await runtime.ProbeAsync(
            CloseConnectionsAction(),
            CancellationToken.None);

        Assert.Equal(TriggerActionProbeStatus.Desired, result.Status);
        Assert.Equal([MihomoServiceIpcCommand.GetConnections], broker.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeCloseConnections_OwnerUnavailableOrUnobservable_ReturnsUnknown(
        bool statusUnobservable)
    {
        MihomoServiceStatus status = statusUnobservable
            ? MihomoServiceStatus.Unknown("unobservable")
            : new MihomoServiceStatus(false, false, "stopped")
            {
                IsScmRunning = false,
            };
        FakeServiceBroker broker = new(status);
        TriggerActionRuntimeAdapter runtime = CreateRuntime(broker);

        TriggerActionProbeResult result = await runtime.ProbeAsync(
            CloseConnectionsAction(),
            CancellationToken.None);

        Assert.Equal(TriggerActionProbeStatus.Unknown, result.Status);
        Assert.Equal("trigger.action.probe_unavailable", result.DiagnosticCode);
        Assert.Empty(broker.Commands);
    }

    [Fact]
    public async Task ApplyCloseConnections_ServiceOwner_ClosesThroughOwnerAwareBroker()
    {
        FakeServiceBroker broker = new(ServiceOwnedStatus());
        TriggerActionRuntimeAdapter runtime = CreateRuntime(broker);
        MutationAdmissionBarrier admission = new();
        using MutationAdmissionLease lease = admission.AcquireOrdinary();

        TriggerActionApplyResult result = await runtime.ApplyAsync(
            CloseConnectionsAction(),
            lease,
            CancellationToken.None);

        Assert.Equal(TriggerActionApplyStatus.Applied, result.Status);
        Assert.Equal([MihomoServiceIpcCommand.CloseAllConnections], broker.Commands);
    }

    [Fact]
    public async Task ApplyCloseConnections_WithoutOwner_PropagatesFailureInsteadOfNoOp()
    {
        FakeServiceBroker broker = new(new MihomoServiceStatus(false, false, "stopped")
        {
            IsScmRunning = false,
        });
        TriggerActionRuntimeAdapter runtime = CreateRuntime(broker);
        MutationAdmissionBarrier admission = new();
        using MutationAdmissionLease lease = admission.AcquireOrdinary();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ApplyAsync(
                CloseConnectionsAction(),
                lease,
                CancellationToken.None));

        Assert.Equal("controller.owner_unavailable", exception.Message);
        Assert.Empty(broker.Commands);
    }

    [Fact]
    public async Task ApplyCloseConnections_BrokerFailure_PropagatesExistingExceptionPolicy()
    {
        FakeServiceBroker broker = new(ServiceOwnedStatus())
        {
            FailureCode = "service.controller.close_failed",
        };
        TriggerActionRuntimeAdapter runtime = CreateRuntime(broker);
        MutationAdmissionBarrier admission = new();
        using MutationAdmissionLease lease = admission.AcquireOrdinary();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ApplyAsync(
                CloseConnectionsAction(),
                lease,
                CancellationToken.None));

        Assert.Equal("service.controller.close_failed", exception.Message);
        Assert.Equal([MihomoServiceIpcCommand.CloseAllConnections], broker.Commands);
    }

    private static TriggerActionRuntimeAdapter CreateRuntime(FakeServiceBroker broker)
    {
        MihomoControllerClient controller = new(
            new HttpClient(new RejectNetworkHandler()),
            new Uri("http://127.0.0.1:9090/"),
            static () => string.Empty,
            static () => false,
            broker);
        ConstructorInfo connectionConstructor = typeof(MihomoConnectionService).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(MihomoControllerClient)],
                modifiers: null)
            ?? throw new InvalidOperationException("MihomoConnectionService test constructor was not found.");
        MihomoConnectionService connections = (MihomoConnectionService)connectionConstructor.Invoke([controller]);

        return new TriggerActionRuntimeAdapter(
            Uninitialized<AppSettingsService>(),
            Uninitialized<StartupLaunchService>(),
            Uninitialized<ConnectionSamplingService>(),
            connections,
            Uninitialized<NetworkStateCoordinator>(),
            new UnusedNetworkStateObserver(),
            new UnusedNotificationSink(),
            new UnusedLifecycleHandoff());
    }

    private static T Uninitialized<T>() where T : class
    {
        return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }

    private static MihomoServiceStatus ServiceOwnedStatus()
    {
        return new MihomoServiceStatus(true, true, "running")
        {
            IsScmRunning = true,
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            ServiceSessionId = ServiceSessionId,
            ServiceVersion = "test",
            ChildState = MihomoServiceChildState.Running,
            ChildProcessId = 42,
            ActiveGeneration = 7,
            ActiveConfigurationHash = ActiveConfigurationHash,
        };
    }

    private static MihomoServiceIpcConnection Connection(string id)
    {
        return new MihomoServiceIpcConnection
        {
            Id = id,
            ProcessName = "test",
            Host = "example.test",
            RuleName = "MATCH",
            RulePayload = string.Empty,
            ProxyName = "DIRECT",
            StartedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private static TriggerOutboxAction CloseConnectionsAction()
    {
        Guid executionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        const long taskRevision = 1;
        const int actionIndex = 0;
        return new TriggerOutboxAction(
            executionId,
            taskRevision,
            actionIndex,
            TriggerIdempotencyKey.Create(executionId, taskRevision, actionIndex),
            new TriggerAction(TriggerActionKind.CloseConnections, new NoActionParameters()),
            TriggerOutboxState.Pending);
    }

    private sealed class FakeServiceBroker(MihomoServiceStatus status)
        : IMihomoControllerServiceBroker
    {
        public IReadOnlyList<MihomoServiceIpcConnection> Connections { get; init; } = [];

        public string? FailureCode { get; init; }

        public List<MihomoServiceIpcCommand> Commands { get; } = [];

        public MihomoServiceStatus GetLatestStatus()
        {
            return status;
        }

        public Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(status);
        }

        public Task<MihomoServiceIpcResponse> SendAsync(
            MihomoServiceIpcCommand command,
            MihomoServiceIpcControllerBinding expectedRuntime,
            string? connectionId,
            MihomoServiceIpcProxySelection? proxySelection,
            MihomoServiceIpcRuntimeLogQuery? runtimeLogQuery,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (FailureCode is not null)
            {
                return Task.FromResult(new MihomoServiceIpcResponse
                {
                    ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                    RequestId = Guid.NewGuid(),
                    Succeeded = false,
                    ErrorCode = FailureCode,
                });
            }

            return Task.FromResult(new MihomoServiceIpcResponse
            {
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                RequestId = Guid.NewGuid(),
                Succeeded = true,
                ConnectionSnapshot = command == MihomoServiceIpcCommand.GetConnections
                    ? new MihomoServiceIpcConnectionSnapshot { Connections = Connections }
                    : null,
            });
        }

        public Task<MihomoServiceIpcResponse> UpdateProviderAsync(
            MihomoServiceIpcControllerBinding expectedRuntime,
            MihomoServiceIpcProviderUpdate providerUpdate,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Provider updates are outside this test seam.");
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The owner-aware test must not use direct HTTP.");
        }
    }

    private sealed class UnusedNetworkStateObserver : INetworkStateObserver
    {
        public Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class UnusedNotificationSink : IIdempotentTriggerNotificationSink
    {
        public Task<bool> IsTriggerNotificationDeliveredAsync(
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeliverTriggerNotificationAsync(
            string idempotencyKey,
            string message,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class UnusedLifecycleHandoff : ITriggerLifecycleHandoff
    {
        public Task<TriggerActionProbeResult> ProbeAsync(
            TriggerOutboxAction action,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TriggerActionApplyResult> HandOffAsync(
            TriggerOutboxAction action,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task AcknowledgeReleaseAsync(
            TriggerLifecycleHandoffIdentity identity,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task AcknowledgeReleasedExecutionAsync(
            TriggerExecution execution,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}

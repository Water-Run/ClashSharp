using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;
using ClashSharp.Tests.Unit.MihomoService;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Contract of the authenticated command processor over the whole command enum: no caller identity
/// other than the exact deployment token can observe or change anything, every structurally invalid
/// request gets a stable refusal without side effects, and lifecycle commands are idempotent.
/// </summary>
public sealed class CommandProcessorContractTests
{
    /// <summary>Gets one row per command value, so a new command is covered automatically.</summary>
    public static TheoryData<MihomoServiceIpcCommand> AllCommands
    {
        get
        {
            TheoryData<MihomoServiceIpcCommand> data = [];
            foreach (MihomoServiceIpcCommand command in Enum.GetValues<MihomoServiceIpcCommand>())
            {
                data.Add(command);
            }

            return data;
        }
    }

    private static string[] HostileTokens()
    {
        string token = MihomoServiceTestSupport.Token;
        return
        [
            string.Empty,
            " ",
            token.ToUpperInvariant(),
            token + "0",
            token[..63],
            new string('0', 64),
            string.Concat(token.Reverse()),
            " " + token,
            token + "\n",
            token + "\0",
            new string('é', 64),
            new string('a', 100_000),
            token[..32] + new string('f', 32),
        ];
    }

    private static MihomoServiceCommandProcessor CreateProcessor(SupervisorHarness harness) =>
        new(harness.Options, harness.Supervisor, harness.Logs, harness.Broker);

    [Theory]
    [MemberData(nameof(AllCommands))]
    public async Task AnyTokenOtherThanTheExactDeploymentToken_IsRefusedWithoutSnapshotOrEffect(
        MihomoServiceIpcCommand command)
    {
        await using SupervisorHarness harness = new();
        MihomoServiceCommandProcessor processor = CreateProcessor(harness);
        int logCount = harness.Logs.ReadLatest(256).Count;
        List<string?> tokens = [.. HostileTokens(), null];

        foreach (string? hostile in tokens)
        {
            MihomoServiceIpcRequest request = Shape(command, harness.Hash) with { AuthenticationToken = hostile! };

            MihomoServiceIpcResponse response = await processor.ProcessAsync(request, CancellationToken.None);

            Assert.False(response.Succeeded);
            Assert.Equal("service.ipc.unauthorized", response.ErrorCode);
            Assert.Null(response.Snapshot);
            Assert.Empty(response.Logs);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Null(response.Validate());
        }

        Assert.Equal(0, harness.Launcher.Attempts);
        Assert.Equal(MihomoServiceChildState.Stopped, harness.Supervisor.GetSnapshot().ChildState);
        Assert.Equal(logCount, harness.Logs.ReadLatest(256).Count);
        Assert.Empty(harness.StagedGenerationFiles());
    }

    [Theory]
    [MemberData(nameof(AllCommands))]
    public async Task EveryCommandWithAValidShape_WhileStopped_ReturnsAValidCorrelatedAnswer(
        MihomoServiceIpcCommand command)
    {
        await using SupervisorHarness harness = new();
        MihomoServiceCommandProcessor processor = CreateProcessor(harness);
        // A hash that does not match the installed file keeps lifecycle commands from starting a child.
        MihomoServiceIpcRequest request = Shape(command, new string('1', 64));

        MihomoServiceIpcResponse response = await processor.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(MihomoServiceIpcProtocol.CurrentVersion, response.ProtocolVersion);
        Assert.Null(response.Validate());
        Assert.NotNull(response.Snapshot);
        Assert.Equal(MihomoServiceChildState.Stopped, response.Snapshot.ChildState);
        Assert.Equal(0, harness.Launcher.Attempts);
        bool expectSuccess = command is MihomoServiceIpcCommand.Hello
            or MihomoServiceIpcCommand.Status
            or MihomoServiceIpcCommand.Stop
            or MihomoServiceIpcCommand.Logs;
        Assert.Equal(expectSuccess, response.Succeeded);
        if (!expectSuccess)
        {
            Assert.False(string.IsNullOrWhiteSpace(response.ErrorCode));
            Assert.Empty(response.Logs);
            Assert.Null(response.EffectiveConfiguration);
            Assert.Null(response.ConnectionSnapshot);
            Assert.Null(response.ProxyRuntimeSnapshot);
            Assert.Null(response.RuntimeLogSnapshot);
        }
    }

    [Theory]
    [MemberData(nameof(AllCommands))]
    public async Task StructurallyInvalidRequests_AreRefusedWithAStableCodeAndNoSideEffect(
        MihomoServiceIpcCommand command)
    {
        await using SupervisorHarness harness = new();
        MihomoServiceCommandProcessor processor = CreateProcessor(harness);
        string hash = harness.Hash;
        MihomoServiceIpcControllerBinding binding = new()
        {
            ServiceSessionId = Guid.NewGuid(),
            Generation = 1,
            ConfigurationHash = hash,
        };
        bool needsGeneration = command is MihomoServiceIpcCommand.Start or MihomoServiceIpcCommand.Reload;
        bool isBroker = command is MihomoServiceIpcCommand.ProbeEffectiveConfiguration
            or MihomoServiceIpcCommand.GetConnections
            or MihomoServiceIpcCommand.CloseConnection
            or MihomoServiceIpcCommand.CloseAllConnections
            or MihomoServiceIpcCommand.GetProxyRuntimeSnapshot
            or MihomoServiceIpcCommand.SelectProxy
            or MihomoServiceIpcCommand.GetRuntimeLogs
            or MihomoServiceIpcCommand.UpdateProvider;
        List<(string Expected, MihomoServiceIpcRequest Request)> cases = [];
        MihomoServiceIpcRequest valid = Shape(command, hash);

        if (needsGeneration)
        {
            cases.Add(("service.ipc.generation_invalid", valid with { Generation = null }));
            cases.Add(("service.ipc.generation_invalid", valid with { Generation = 0 }));
            cases.Add(("service.ipc.generation_invalid", valid with { Generation = -5 }));
            cases.Add(("service.ipc.generation_invalid", valid with { ConfigurationHash = null }));
            cases.Add(("service.ipc.generation_invalid", valid with { ConfigurationHash = hash.ToUpperInvariant() }));
            cases.Add(("service.ipc.generation_invalid", valid with { ConfigurationHash = hash[..63] }));
            cases.Add(("service.ipc.generation_invalid", valid with { DataGenerationId = Guid.Empty }));
        }
        else
        {
            cases.Add(("service.ipc.generation_unexpected", valid with { Generation = 1 }));
            cases.Add(("service.ipc.generation_unexpected", valid with { ConfigurationHash = hash }));
            cases.Add(("service.ipc.generation_unexpected", valid with { DataGenerationId = Guid.NewGuid() }));
        }

        if (isBroker)
        {
            cases.Add(("service.ipc.expected_runtime_invalid", valid with { ExpectedRuntime = null }));
            cases.Add(("service.ipc.expected_runtime_invalid", valid with { ExpectedRuntime = binding with { Generation = 0 } }));
            cases.Add(("service.ipc.expected_runtime_invalid", valid with { ExpectedRuntime = binding with { ServiceSessionId = Guid.Empty } }));
            cases.Add(("service.ipc.expected_runtime_invalid", valid with { ExpectedRuntime = binding with { ConfigurationHash = "x" } }));
            cases.Add(("service.ipc.expected_runtime_invalid", valid with { ExpectedRuntime = binding with { DataGenerationId = Guid.Empty } }));
        }
        else
        {
            cases.Add(("service.ipc.expected_runtime_unexpected", valid with { ExpectedRuntime = binding }));
        }

        if (command == MihomoServiceIpcCommand.CloseConnection)
        {
            cases.Add(("service.ipc.connection_id_invalid", valid with { ConnectionId = null }));
            cases.Add(("service.ipc.connection_id_invalid", valid with { ConnectionId = " " }));
            cases.Add(("service.ipc.connection_id_invalid", valid with { ConnectionId = "a\nb" }));
            cases.Add(("service.ipc.connection_id_invalid", valid with { ConnectionId = new string('i', 513) }));
        }
        else
        {
            cases.Add(("service.ipc.connection_id_unexpected", valid with { ConnectionId = "c" }));
        }

        if (command == MihomoServiceIpcCommand.SelectProxy)
        {
            cases.Add(("service.ipc.proxy_selection_invalid", valid with { ProxySelection = null }));
            cases.Add(("service.ipc.proxy_selection_invalid", valid with { ProxySelection = new() { GroupName = "", ProxyName = "p" } }));
            cases.Add(("service.ipc.proxy_selection_invalid", valid with { ProxySelection = new() { GroupName = "g", ProxyName = "p\t" } }));
        }
        else
        {
            cases.Add(("service.ipc.proxy_selection_unexpected", valid with { ProxySelection = new() { GroupName = "g", ProxyName = "p" } }));
        }

        if (command == MihomoServiceIpcCommand.GetRuntimeLogs)
        {
            cases.Add(("service.ipc.runtime_log_query_invalid", valid with { RuntimeLogQuery = null }));
            cases.Add(("service.ipc.runtime_log_query_invalid", valid with { RuntimeLogQuery = new() { AfterSequence = -1, MaximumEntries = 10 } }));
            cases.Add(("service.ipc.runtime_log_query_invalid", valid with { RuntimeLogQuery = new() { AfterSequence = 0, MaximumEntries = 0 } }));
            cases.Add(("service.ipc.runtime_log_query_invalid", valid with { RuntimeLogQuery = new() { AfterSequence = 0, MaximumEntries = MihomoServiceIpcProtocol.MaximumRuntimeLogEntries + 1 } }));
        }
        else
        {
            cases.Add(("service.ipc.runtime_log_query_unexpected", valid with { RuntimeLogQuery = new() { AfterSequence = 0, MaximumEntries = 1 } }));
        }

        if (command == MihomoServiceIpcCommand.UpdateProvider)
        {
            cases.Add(("service.ipc.provider_update_invalid", valid with { ProviderUpdate = null }));
            cases.Add(("service.ipc.provider_update_invalid", valid with { ProviderUpdate = new() { Kind = (MihomoServiceIpcProviderKind)99, Name = "p" } }));
            cases.Add(("service.ipc.provider_update_invalid", valid with { ProviderUpdate = new() { Kind = MihomoServiceIpcProviderKind.Rule, Name = "" } }));
        }
        else
        {
            cases.Add(("service.ipc.provider_update_unexpected", valid with { ProviderUpdate = new() { Kind = MihomoServiceIpcProviderKind.Proxy, Name = "p" } }));
        }

        if (command == MihomoServiceIpcCommand.Logs)
        {
            cases.Add(("service.ipc.log_limit_invalid", valid with { MaximumLogEntries = null }));
            cases.Add(("service.ipc.log_limit_invalid", valid with { MaximumLogEntries = 0 }));
            cases.Add(("service.ipc.log_limit_invalid", valid with { MaximumLogEntries = MihomoServiceIpcProtocol.MaximumLogEntries + 1 }));
        }
        else
        {
            cases.Add(("service.ipc.log_limit_unexpected", valid with { MaximumLogEntries = 5 }));
        }

        cases.Add(("service.ipc.command_invalid", valid with { Command = (MihomoServiceIpcCommand)(-1) }));
        cases.Add(("service.ipc.command_invalid", valid with { Command = (MihomoServiceIpcCommand)9999 }));

        foreach ((string expected, MihomoServiceIpcRequest request) in cases)
        {
            MihomoServiceIpcResponse response = await processor.ProcessAsync(request, CancellationToken.None);

            Assert.False(response.Succeeded);
            Assert.Equal(expected, response.ErrorCode);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(MihomoServiceChildState.Stopped, response.Snapshot!.ChildState);
            Assert.Null(response.Validate());
        }

        Assert.Equal(0, harness.Launcher.Attempts);
        Assert.Empty(harness.StagedGenerationFiles());
        Assert.Empty(harness.EffectiveFiles());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(MihomoServiceIpcProtocol.CurrentVersion - 1)]
    [InlineData(MihomoServiceIpcProtocol.CurrentVersion + 1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task AnyProtocolVersionButTheCurrentOne_IsRefusedForEveryCommandWithoutSideEffect(int version)
    {
        await using SupervisorHarness harness = new();
        MihomoServiceCommandProcessor processor = CreateProcessor(harness);

        foreach (MihomoServiceIpcCommand command in Enum.GetValues<MihomoServiceIpcCommand>())
        {
            MihomoServiceIpcRequest request = Shape(command, harness.Hash) with { ProtocolVersion = version };

            MihomoServiceIpcResponse response = await processor.ProcessAsync(request, CancellationToken.None);

            Assert.False(response.Succeeded, command.ToString());
            Assert.Equal("service.ipc.protocol_incompatible", response.ErrorCode);
            Assert.Equal(MihomoServiceIpcProtocol.CurrentVersion, response.ProtocolVersion);
            Assert.Equal(request.RequestId, response.RequestId);
        }

        Assert.Equal(0, harness.Launcher.Attempts);
    }

    [Fact]
    public async Task RequestWithoutCorrelationIdentity_IsRejectedBeforeAnyEffectForEveryCommand()
    {
        await using SupervisorHarness harness = new();
        MihomoServiceCommandProcessor processor = CreateProcessor(harness);

        foreach (MihomoServiceIpcCommand command in Enum.GetValues<MihomoServiceIpcCommand>())
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
                Shape(command, harness.Hash) with { RequestId = Guid.Empty },
                CancellationToken.None));
        }

        Assert.Equal(0, harness.Launcher.Attempts);
    }

    [Fact]
    public async Task LifecycleCommands_AreIdempotentWhenRepeated()
    {
        await using SupervisorHarness harness = new();
        MihomoServiceCommandProcessor processor = CreateProcessor(harness);
        MihomoServiceIpcRequest start = Shape(MihomoServiceIpcCommand.Start, harness.Hash);

        MihomoServiceIpcResponse first = await processor.ProcessAsync(start with { RequestId = Guid.NewGuid() }, CancellationToken.None);
        MihomoServiceIpcResponse second = await processor.ProcessAsync(start with { RequestId = Guid.NewGuid() }, CancellationToken.None);
        MihomoServiceIpcResponse reload = await processor.ProcessAsync(
            Shape(MihomoServiceIpcCommand.Reload, harness.Hash),
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(reload.Succeeded);
        Assert.Equal(1, harness.Launcher.Attempts);
        Assert.Equal(first.Snapshot, second.Snapshot);
        Assert.Equal(first.Snapshot, reload.Snapshot);

        for (int index = 0; index < 3; index++)
        {
            MihomoServiceIpcResponse stop = await processor.ProcessAsync(
                Shape(MihomoServiceIpcCommand.Stop, harness.Hash),
                CancellationToken.None);
            Assert.True(stop.Succeeded);
            Assert.Equal(MihomoServiceChildState.Stopped, stop.Snapshot!.ChildState);
        }

        Assert.Equal(1, harness.Launcher.Snapshot().Single().StopCalls);
        Assert.Empty(harness.EffectiveFiles());

        MihomoServiceIpcResponse restarted = await processor.ProcessAsync(
            start with { RequestId = Guid.NewGuid() },
            CancellationToken.None);
        Assert.True(restarted.Succeeded);
        Assert.Equal(2, harness.Launcher.Attempts);
    }

    [Fact]
    public async Task ReorderedLifecycleCommands_NeverBreakTheStateMachine()
    {
        await using SupervisorHarness harness = new();
        MihomoServiceCommandProcessor processor = CreateProcessor(harness);

        // Stop before any start is a successful no-op; reload before start launches.
        Assert.True((await processor.ProcessAsync(Shape(MihomoServiceIpcCommand.Stop, harness.Hash), CancellationToken.None)).Succeeded);
        MihomoServiceIpcResponse reload = await processor.ProcessAsync(
            Shape(MihomoServiceIpcCommand.Reload, harness.Hash),
            CancellationToken.None);
        Assert.True(reload.Succeeded);
        Assert.Equal(MihomoServiceChildState.Running, reload.Snapshot!.ChildState);
        Assert.Equal(1, harness.Launcher.Attempts);

        // A start for another generation while running is refused and the child is untouched.
        MihomoServiceIpcResponse refused = await processor.ProcessAsync(
            Shape(MihomoServiceIpcCommand.Start, harness.Hash) with { Generation = 2 },
            CancellationToken.None);
        Assert.False(refused.Succeeded);
        Assert.Equal("service.child.already_running", refused.ErrorCode);
        Assert.Equal(reload.Snapshot.ChildProcessId, refused.Snapshot!.ChildProcessId);
        Assert.Equal(0, harness.Launcher.Snapshot().Single().StopCalls);

        // A reload that rebinds generation 1 to different bytes is a conflict and preserves the child.
        string changedHash = harness.WriteNewConfiguration();
        MihomoServiceIpcResponse conflict = await processor.ProcessAsync(
            Shape(MihomoServiceIpcCommand.Reload, changedHash),
            CancellationToken.None);
        Assert.False(conflict.Succeeded);
        Assert.Equal("service.child.generation_conflict", conflict.ErrorCode);
        Assert.Equal(MihomoServiceChildState.Running, conflict.Snapshot!.ChildState);
        Assert.Equal(reload.Snapshot.ChildProcessId, conflict.Snapshot.ChildProcessId);
        Assert.Equal(0, harness.Launcher.Snapshot().Single().StopCalls);

        MihomoServiceIpcResponse logs = await processor.ProcessAsync(
            Shape(MihomoServiceIpcCommand.Logs, changedHash),
            CancellationToken.None);
        Assert.True(logs.Succeeded);
        Assert.True(logs.Logs.Count <= 10);
    }

    [Fact]
    public async Task EveryResponseCarriesTheSameServiceSession_ForAnyInterleavingOfReadCommands()
    {
        await using SupervisorHarness harness = new();
        MihomoServiceCommandProcessor processor = CreateProcessor(harness);
        Random random = new(20261109);
        MihomoServiceIpcCommand[] commands =
        [
            MihomoServiceIpcCommand.Hello,
            MihomoServiceIpcCommand.Status,
            MihomoServiceIpcCommand.Logs,
            MihomoServiceIpcCommand.Stop,
        ];
        Guid? session = null;

        for (int index = 0; index < 100; index++)
        {
            MihomoServiceIpcCommand command = commands[random.Next(commands.Length)];
            MihomoServiceIpcResponse response = await processor.ProcessAsync(
                Shape(command, harness.Hash),
                CancellationToken.None);

            Assert.True(response.Succeeded);
            session ??= response.Snapshot!.SessionId;
            Assert.Equal(session, response.Snapshot!.SessionId);
        }
    }

    internal static MihomoServiceIpcRequest Shape(MihomoServiceIpcCommand command, string hash)
    {
        MihomoServiceIpcRequest request = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = MihomoServiceTestSupport.Token,
            Command = command,
        };
        MihomoServiceIpcControllerBinding binding = new()
        {
            ServiceSessionId = Guid.NewGuid(),
            Generation = 1,
            ConfigurationHash = hash,
        };
        return command switch
        {
            MihomoServiceIpcCommand.Hello or MihomoServiceIpcCommand.Status or MihomoServiceIpcCommand.Stop => request,
            MihomoServiceIpcCommand.Start or MihomoServiceIpcCommand.Reload => request with
            {
                Generation = 1,
                ConfigurationHash = hash,
            },
            MihomoServiceIpcCommand.Logs => request with { MaximumLogEntries = 10 },
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration
                or MihomoServiceIpcCommand.GetConnections
                or MihomoServiceIpcCommand.CloseAllConnections
                or MihomoServiceIpcCommand.GetProxyRuntimeSnapshot => request with { ExpectedRuntime = binding },
            MihomoServiceIpcCommand.CloseConnection => request with
            {
                ExpectedRuntime = binding,
                ConnectionId = "connection-1",
            },
            MihomoServiceIpcCommand.SelectProxy => request with
            {
                ExpectedRuntime = binding,
                ProxySelection = new MihomoServiceIpcProxySelection { GroupName = "group", ProxyName = "proxy" },
            },
            MihomoServiceIpcCommand.GetRuntimeLogs => request with
            {
                ExpectedRuntime = binding,
                RuntimeLogQuery = new MihomoServiceIpcRuntimeLogQuery { AfterSequence = 0, MaximumEntries = 10 },
            },
            MihomoServiceIpcCommand.UpdateProvider => request with
            {
                ExpectedRuntime = binding,
                ProviderUpdate = new MihomoServiceIpcProviderUpdate
                {
                    Kind = MihomoServiceIpcProviderKind.Proxy,
                    Name = "provider",
                },
            },
            _ => throw new InvalidOperationException(
                $"Command {command} has no request shape in this contract; add one."),
        };
    }
}

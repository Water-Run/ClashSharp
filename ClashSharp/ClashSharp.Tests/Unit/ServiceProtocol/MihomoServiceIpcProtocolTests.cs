using System.Buffers.Binary;
using System.Text;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.ServiceProtocol;

/// <summary>Verifies the bounded, versioned mihomo service IPC wire contract.</summary>
public sealed class MihomoServiceIpcProtocolTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void NormalizeRuntimeLogMessage_BoundsControlsAndUtf16Boundary()
    {
        string source = new string(
            'x',
            MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters - 1)
            + "😀\r\ntrailing";

        string normalized = MihomoServiceIpcProtocol.NormalizeRuntimeLogMessage(source);

        Assert.Equal(MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters - 1, normalized.Length);
        Assert.DoesNotContain(normalized, char.IsControl);
        Assert.False(char.IsSurrogate(normalized[^1]));
    }

    /// <summary>Verifies pipe names are stable, owner-specific, and do not disclose the SID.</summary>
    [Fact]
    public void BuildPipeName_HashesOwnerSidDeterministically()
    {
        const string firstSid = "S-1-5-21-100-200-300-1001";
        const string secondSid = "S-1-5-21-100-200-300-1002";

        string first = MihomoServiceIpcProtocol.BuildPipeName(firstSid, Token);

        Assert.Equal(first, MihomoServiceIpcProtocol.BuildPipeName(firstSid, Token));
        Assert.NotEqual(first, MihomoServiceIpcProtocol.BuildPipeName(secondSid, Token));
        Assert.DoesNotContain(firstSid, first, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, first, StringComparison.Ordinal);
        Assert.StartsWith("ClashSharp.Mihomo.", first, StringComparison.Ordinal);
    }

    /// <summary>Verifies activation requests cannot omit generation identity or exact bytes.</summary>
    [Fact]
    public void Validate_StartRequiresGenerationAndConfigurationHash()
    {
        MihomoServiceIpcRequest request = CreateRequest(MihomoServiceIpcCommand.Start);

        Assert.Equal("service.ipc.generation_invalid", request.Validate());

        request = request with { Generation = 7, ConfigurationHash = Hash };

        Assert.Null(request.Validate());
    }

    /// <summary>Verifies non-activation operations cannot smuggle unused generation fields.</summary>
    [Fact]
    public void Validate_StatusRejectsUnexpectedGeneration()
    {
        MihomoServiceIpcRequest request = CreateRequest(MihomoServiceIpcCommand.Status) with
        {
            Generation = 7,
            ConfigurationHash = Hash,
        };

        Assert.Equal("service.ipc.generation_unexpected", request.Validate());
    }

    /// <summary>Verifies every broker operation is bound to one exact service runtime.</summary>
    [Theory]
    [InlineData(MihomoServiceIpcCommand.ProbeEffectiveConfiguration)]
    [InlineData(MihomoServiceIpcCommand.GetConnections)]
    [InlineData(MihomoServiceIpcCommand.CloseConnection)]
    [InlineData(MihomoServiceIpcCommand.CloseAllConnections)]
    [InlineData(MihomoServiceIpcCommand.GetProxyRuntimeSnapshot)]
    [InlineData(MihomoServiceIpcCommand.SelectProxy)]
    [InlineData(MihomoServiceIpcCommand.GetRuntimeLogs)]
    [InlineData(MihomoServiceIpcCommand.UpdateProvider)]
    public void Validate_BrokerCommandsRequireExactRuntimeBinding(
        MihomoServiceIpcCommand command)
    {
        MihomoServiceIpcRequest request = AddCommandPayload(CreateRequest(command));

        Assert.Equal("service.ipc.expected_runtime_invalid", request.Validate());

        request = request with { ExpectedRuntime = CreateBinding() };

        Assert.Null(request.Validate());
    }

    /// <summary>Verifies lifecycle operations cannot smuggle broker-only runtime identity.</summary>
    [Fact]
    public void Validate_LifecycleCommandRejectsControllerBinding()
    {
        MihomoServiceIpcRequest request = CreateRequest(MihomoServiceIpcCommand.Status) with
        {
            ExpectedRuntime = CreateBinding(),
        };

        Assert.Equal("service.ipc.expected_runtime_unexpected", request.Validate());
    }

    /// <summary>Verifies close-one accepts only a bounded typed connection id.</summary>
    [Fact]
    public void Validate_CloseConnectionRejectsOversizedIdentifier()
    {
        MihomoServiceIpcRequest request = CreateBrokerRequest(
            MihomoServiceIpcCommand.CloseConnection) with
        {
            ConnectionId = new string(
                'x',
                MihomoServiceIpcProtocol.MaximumControllerIdentifierCharacters + 1),
        };

        Assert.Equal("service.ipc.connection_id_invalid", request.Validate());
    }

    /// <summary>Verifies proxy selection has no arbitrary body beyond two bounded selectors.</summary>
    [Fact]
    public void Validate_SelectProxyRejectsInvalidTypedSelection()
    {
        MihomoServiceIpcRequest request = CreateBrokerRequest(
            MihomoServiceIpcCommand.SelectProxy) with
        {
            ProxySelection = new MihomoServiceIpcProxySelection
            {
                GroupName = "Proxy\nGroup",
                ProxyName = "Node A",
            },
        };

        Assert.Equal("service.ipc.proxy_selection_invalid", request.Validate());
    }

    /// <summary>Verifies typed runtime-log polling has a nonnegative cursor and bounded page size.</summary>
    [Fact]
    public void Validate_RuntimeLogsRejectsInvalidCursorPage()
    {
        MihomoServiceIpcRequest request = CreateBrokerRequest(
            MihomoServiceIpcCommand.GetRuntimeLogs) with
        {
            RuntimeLogQuery = new MihomoServiceIpcRuntimeLogQuery
            {
                AfterSequence = -1,
                MaximumEntries = MihomoServiceIpcProtocol.MaximumRuntimeLogEntries + 1,
            },
        };

        Assert.Equal("service.ipc.runtime_log_query_invalid", request.Validate());
    }

    /// <summary>Verifies provider mutation exposes only a kind and bounded provider name.</summary>
    [Fact]
    public void Validate_UpdateProviderRejectsInvalidTypedIdentifier()
    {
        MihomoServiceIpcRequest request = CreateBrokerRequest(
            MihomoServiceIpcCommand.UpdateProvider) with
        {
            ProviderUpdate = new MihomoServiceIpcProviderUpdate
            {
                Kind = MihomoServiceIpcProviderKind.Proxy,
                Name = "provider\nname",
            },
        };

        Assert.Equal("service.ipc.provider_update_invalid", request.Validate());
    }

    /// <summary>Verifies requests round-trip through the strict length-prefixed frame.</summary>
    [Fact]
    public async Task FrameCodec_RequestRoundTrips()
    {
        MihomoServiceIpcRequest expected = CreateRequest(MihomoServiceIpcCommand.Reload) with
        {
            Generation = 19,
            ConfigurationHash = Hash,
        };
        using MemoryStream stream = new();

        await MihomoServiceIpcFrameCodec.WriteRequestAsync(
            stream,
            expected,
            CancellationToken.None);
        stream.Position = 0;
        MihomoServiceIpcRequest actual = await MihomoServiceIpcFrameCodec.ReadRequestAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Null(actual.Validate());
    }

    /// <summary>Verifies a typed broker request round-trips without method, path, or raw body fields.</summary>
    [Fact]
    public async Task FrameCodec_BrokerRequestRoundTrips()
    {
        MihomoServiceIpcRequest expected = CreateBrokerRequest(
            MihomoServiceIpcCommand.SelectProxy);
        using MemoryStream stream = new();

        await MihomoServiceIpcFrameCodec.WriteRequestAsync(
            stream,
            expected,
            CancellationToken.None);
        stream.Position = 0;
        MihomoServiceIpcRequest actual = await MihomoServiceIpcFrameCodec.ReadRequestAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Null(actual.Validate());
    }

    /// <summary>Verifies the frame reader rejects lengths before allocating unbounded memory.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MihomoServiceIpcProtocol.MaximumFrameBytes + 1)]
    public async Task FrameCodec_RejectsInvalidLength(int length)
    {
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using MemoryStream stream = new(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MihomoServiceIpcFrameCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    /// <summary>Verifies strict JSON rejects unknown members instead of silently downgrading.</summary>
    [Fact]
    public async Task FrameCodec_RejectsUnknownJsonMember()
    {
        const string json = "{\"protocolVersion\":1,\"requestId\":\"300da140-b7b3-44b9-8d4f-aa17cf26bd64\",\"authenticationToken\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"command\":1,\"generation\":null,\"configurationHash\":null,\"maximumLogEntries\":null,\"unexpected\":true}";
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        using MemoryStream stream = new(frame);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MihomoServiceIpcFrameCodec.ReadRequestAsync(stream, CancellationToken.None));

        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    /// <summary>Verifies the wire shape has no escape hatch for arbitrary controller requests.</summary>
    [Theory]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("rawPayload")]
    public async Task FrameCodec_RejectsArbitraryControllerTransportMembers(string memberName)
    {
        string json = "{\"protocolVersion\":2,"
            + "\"requestId\":\"300da140-b7b3-44b9-8d4f-aa17cf26bd64\","
            + $"\"authenticationToken\":\"{Token}\","
            + "\"command\":6,"
            + "\"expectedRuntime\":{"
            + "\"serviceSessionId\":\"06d53110-d096-4e5a-bc20-aa32d74fec15\","
            + "\"generation\":7,"
            + $"\"configurationHash\":\"{Hash}\"}},"
            + $"\"{memberName}\":\"GET /configs\"}}";
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        using MemoryStream stream = new(frame);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MihomoServiceIpcFrameCodec.ReadRequestAsync(stream, CancellationToken.None));

        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    /// <summary>Verifies duplicate members cannot override an authenticated request field.</summary>
    [Fact]
    public async Task FrameCodec_RejectsDuplicateJsonMember()
    {
        const string json = "{\"protocolVersion\":1,\"protocolVersion\":1,\"requestId\":\"300da140-b7b3-44b9-8d4f-aa17cf26bd64\",\"authenticationToken\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"command\":1,\"generation\":null,\"configurationHash\":null,\"maximumLogEntries\":null}";
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        using MemoryStream stream = new(frame);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MihomoServiceIpcFrameCodec.ReadRequestAsync(stream, CancellationToken.None));

        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    /// <summary>Verifies response validation enforces the exact payload for the original command.</summary>
    [Fact]
    public void ResponseValidateFor_RequiresCommandSpecificPayload()
    {
        MihomoServiceIpcRequest request = CreateBrokerRequest(
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration);
        MihomoServiceIpcResponse response = CreateBrokerResponse(request) with
        {
            EffectiveConfiguration = new MihomoServiceIpcEffectiveConfiguration
            {
                ControllerReady = true,
                Mode = MihomoServiceIpcRoutingMode.Rule,
                TunEnabled = true,
                MixedPort = 7890,
            },
        };

        Assert.Null(response.Validate());
        Assert.Null(response.ValidateFor(request));

        response = response with
        {
            EffectiveConfiguration = null,
            ConnectionSnapshot = new MihomoServiceIpcConnectionSnapshot(),
        };

        Assert.Equal(
            "service.ipc.response_command_payload_invalid",
            response.ValidateFor(request));
    }

    /// <summary>Verifies successful broker responses echo the exact running session and generation.</summary>
    [Fact]
    public void ResponseValidateFor_RejectsStaleRuntimeSnapshot()
    {
        MihomoServiceIpcRequest request = CreateBrokerRequest(
            MihomoServiceIpcCommand.GetConnections);
        MihomoServiceIpcResponse response = CreateBrokerResponse(request) with
        {
            Snapshot = CreateRunningSnapshot(CreateBinding() with { Generation = 9 }),
            ConnectionSnapshot = new MihomoServiceIpcConnectionSnapshot(),
        };

        Assert.Equal(
            "service.ipc.response_runtime_binding_invalid",
            response.ValidateFor(request));
    }

    /// <summary>Verifies typed controller payload kinds are mutually exclusive.</summary>
    [Fact]
    public void ResponseValidate_RejectsConflictingControllerPayloads()
    {
        MihomoServiceIpcResponse response = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            Succeeded = true,
            EffectiveConfiguration = new MihomoServiceIpcEffectiveConfiguration
            {
                ControllerReady = true,
                Mode = MihomoServiceIpcRoutingMode.Direct,
                MixedPort = 7890,
            },
            ConnectionSnapshot = new MihomoServiceIpcConnectionSnapshot(),
        };

        Assert.Equal("service.ipc.response_payload_conflict", response.Validate());
    }

    /// <summary>Verifies failure responses cannot carry controller data from a partial operation.</summary>
    [Fact]
    public void ResponseValidate_RejectsPayloadOnFailure()
    {
        MihomoServiceIpcResponse response = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            Succeeded = false,
            ErrorCode = "service.controller.not_ready",
            ConnectionSnapshot = new MihomoServiceIpcConnectionSnapshot(),
        };

        Assert.Equal("service.ipc.response_failure_payload_invalid", response.Validate());
    }

    /// <summary>Verifies cursor validation rejects replayed and oversized runtime-log pages.</summary>
    [Fact]
    public void ResponseValidateFor_RejectsInvalidRuntimeLogPage()
    {
        MihomoServiceIpcRequest request = CreateBrokerRequest(
            MihomoServiceIpcCommand.GetRuntimeLogs) with
        {
            RuntimeLogQuery = new MihomoServiceIpcRuntimeLogQuery
            {
                AfterSequence = 40,
                MaximumEntries = 1,
            },
        };
        MihomoServiceIpcResponse response = CreateBrokerResponse(request) with
        {
            RuntimeLogSnapshot = new MihomoServiceIpcRuntimeLogSnapshot
            {
                LatestSequence = 42,
                Entries =
                [
                    CreateRuntimeLogEntry(40),
                    CreateRuntimeLogEntry(42),
                ],
            },
        };

        Assert.Null(response.Validate());
        Assert.Equal(
            "service.ipc.response_runtime_log_cursor_invalid",
            response.ValidateFor(request));
    }

    /// <summary>Verifies aggregate character limits apply below the outer frame-size ceiling.</summary>
    [Fact]
    public void ConnectionSnapshotValidate_RejectsAggregateTextOverflow()
    {
        string text = new('x', MihomoServiceIpcProtocol.MaximumControllerTextCharacters);
        MihomoServiceIpcConnectionSnapshot snapshot = new()
        {
            Connections = Enumerable.Range(1, 16)
                .Select(index => new MihomoServiceIpcConnection
                {
                    Id = $"connection-{index}",
                    ProcessName = text,
                    Host = text,
                    RuleName = text,
                    RulePayload = text,
                    ProxyName = text,
                    StartedAt = DateTimeOffset.UnixEpoch,
                })
                .ToArray(),
        };

        Assert.Equal("service.ipc.controller_aggregate_invalid", snapshot.Validate());
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\tvalue")]
    public void TypedControllerPayloads_RejectControlCharacters(string unsafeText)
    {
        MihomoServiceIpcConnection connection = new()
        {
            Id = "connection-one",
            ProcessName = unsafeText,
            Host = "example.com",
            RuleName = "MATCH",
            RulePayload = string.Empty,
            ProxyName = "DIRECT",
            StartedAt = DateTimeOffset.UnixEpoch,
        };
        MihomoServiceIpcRuntimeLogEntry log = new()
        {
            Sequence = 1,
            Level = MihomoServiceIpcRuntimeLogLevel.Information,
            Message = unsafeText,
        };

        Assert.Equal("service.ipc.connection_invalid", connection.Validate());
        Assert.Equal("service.ipc.runtime_log_entry_invalid", log.Validate());
    }

    [Fact]
    public void EffectiveConfigurationValidate_AllowsDisabledServiceMixedListener()
    {
        MihomoServiceIpcEffectiveConfiguration effective = new()
        {
            ControllerReady = true,
            Mode = MihomoServiceIpcRoutingMode.Rule,
            TunEnabled = true,
            MixedPort = 0,
        };

        Assert.Null(effective.Validate());
    }

    /// <summary>Verifies one proxy group cannot exceed its typed candidate cap.</summary>
    [Fact]
    public void ProxyGroupValidate_RejectsCandidateOverflow()
    {
        MihomoServiceIpcProxyGroup group = new()
        {
            Name = "Proxy",
            Type = "Selector",
            CurrentSelection = "node-0",
            Candidates = Enumerable
                .Range(0, MihomoServiceIpcProtocol.MaximumControllerCandidatesPerGroup + 1)
                .Select(index => $"node-{index}")
                .ToArray(),
        };

        Assert.Equal("service.ipc.proxy_group_invalid", group.Validate());
    }

    /// <summary>Verifies a typed proxy response round-trips through the strict frame.</summary>
    [Fact]
    public async Task FrameCodec_TypedProxyResponseRoundTrips()
    {
        MihomoServiceIpcRequest request = CreateBrokerRequest(
            MihomoServiceIpcCommand.GetProxyRuntimeSnapshot);
        MihomoServiceIpcResponse expected = CreateBrokerResponse(request) with
        {
            ProxyRuntimeSnapshot = new MihomoServiceIpcProxyRuntimeSnapshot
            {
                Groups =
                [
                    new MihomoServiceIpcProxyGroup
                    {
                        Name = "Proxy",
                        Type = "Selector",
                        CurrentSelection = "Node A",
                        Candidates = ["Node A", "DIRECT"],
                    },
                ],
                Providers =
                [
                    new MihomoServiceIpcProvider
                    {
                        Name = "subscription",
                        Kind = MihomoServiceIpcProviderKind.Proxy,
                        VehicleType = "HTTP",
                        ItemCount = 2,
                        UpdatedAt = DateTimeOffset.UnixEpoch,
                    },
                ],
            },
        };
        using MemoryStream stream = new();

        await MihomoServiceIpcFrameCodec.WriteResponseAsync(
            stream,
            expected,
            CancellationToken.None);
        stream.Position = 0;
        MihomoServiceIpcResponse actual = await MihomoServiceIpcFrameCodec.ReadResponseAsync(
            stream,
            CancellationToken.None);

        Assert.Single(actual.ProxyRuntimeSnapshot!.Groups);
        Assert.Single(actual.ProxyRuntimeSnapshot.Providers);
        Assert.Null(actual.ValidateFor(request));
    }

    /// <summary>Verifies the writer rejects an encoded frame above the hard byte ceiling.</summary>
    [Fact]
    public async Task FrameCodec_RejectsOversizedResponseOnWrite()
    {
        MihomoServiceIpcResponse response = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            Succeeded = true,
            Logs = Enumerable
                .Repeat(
                    new string('x', MihomoServiceIpcProtocol.MaximumLogEntryCharacters),
                    MihomoServiceIpcProtocol.MaximumLogEntries)
                .ToArray(),
        };
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MihomoServiceIpcFrameCodec.WriteResponseAsync(
                stream,
                response,
                CancellationToken.None));
    }

    /// <summary>Verifies running snapshots require a PID and exact active generation identity.</summary>
    [Fact]
    public void SnapshotValidate_RunningRequiresOwnedGeneration()
    {
        MihomoServiceIpcSnapshot snapshot = new()
        {
            SessionId = Guid.NewGuid(),
            ServiceVersion = "1.0.0",
            ChildState = MihomoServiceChildState.Running,
        };

        Assert.Equal("service.ipc.active_generation_invalid", snapshot.Validate());

        snapshot = snapshot with
        {
            ChildProcessId = 42,
            ActiveGeneration = 3,
            ActiveConfigurationHash = Hash,
        };

        Assert.Null(snapshot.Validate());
    }

    /// <summary>Verifies a fault may retain the PID when failed Job shutdown still owns a child.</summary>
    [Fact]
    public void SnapshotValidate_FaultedMayRetainOwnedProcess()
    {
        MihomoServiceIpcSnapshot snapshot = new()
        {
            SessionId = Guid.NewGuid(),
            ServiceVersion = "1.0.0",
            ChildState = MihomoServiceChildState.Faulted,
            ChildProcessId = 42,
            ActiveGeneration = 3,
            ActiveConfigurationHash = Hash,
            FaultCode = "service.child.stop_failed",
        };

        Assert.Null(snapshot.Validate());
    }

    private static MihomoServiceIpcRequest CreateRequest(MihomoServiceIpcCommand command)
    {
        return new MihomoServiceIpcRequest
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = Token,
            Command = command,
            MaximumLogEntries = command == MihomoServiceIpcCommand.Logs ? 100 : null,
        };
    }

    private static MihomoServiceIpcRequest CreateBrokerRequest(
        MihomoServiceIpcCommand command)
    {
        return AddCommandPayload(CreateRequest(command) with
        {
            ExpectedRuntime = CreateBinding(),
        });
    }

    private static MihomoServiceIpcRequest AddCommandPayload(MihomoServiceIpcRequest request)
    {
        return request.Command switch
        {
            MihomoServiceIpcCommand.CloseConnection => request with
            {
                ConnectionId = "connection-1",
            },
            MihomoServiceIpcCommand.SelectProxy => request with
            {
                ProxySelection = new MihomoServiceIpcProxySelection
                {
                    GroupName = "Proxy",
                    ProxyName = "Node A",
                },
            },
            MihomoServiceIpcCommand.GetRuntimeLogs => request with
            {
                RuntimeLogQuery = new MihomoServiceIpcRuntimeLogQuery
                {
                    AfterSequence = 0,
                    MaximumEntries = 100,
                },
            },
            MihomoServiceIpcCommand.UpdateProvider => request with
            {
                ProviderUpdate = new MihomoServiceIpcProviderUpdate
                {
                    Kind = MihomoServiceIpcProviderKind.Proxy,
                    Name = "subscription",
                },
            },
            _ => request,
        };
    }

    private static MihomoServiceIpcControllerBinding CreateBinding()
    {
        return new MihomoServiceIpcControllerBinding
        {
            ServiceSessionId = Guid.Parse("06d53110-d096-4e5a-bc20-aa32d74fec15"),
            Generation = 7,
            ConfigurationHash = Hash,
        };
    }

    private static MihomoServiceIpcSnapshot CreateRunningSnapshot(
        MihomoServiceIpcControllerBinding binding)
    {
        return new MihomoServiceIpcSnapshot
        {
            SessionId = binding.ServiceSessionId,
            ServiceVersion = "2.0.0",
            ChildState = MihomoServiceChildState.Running,
            ChildProcessId = 42,
            ActiveGeneration = binding.Generation,
            ActiveConfigurationHash = binding.ConfigurationHash,
        };
    }

    private static MihomoServiceIpcResponse CreateBrokerResponse(
        MihomoServiceIpcRequest request)
    {
        return new MihomoServiceIpcResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            RequestId = request.RequestId,
            Succeeded = true,
            Snapshot = CreateRunningSnapshot(request.ExpectedRuntime!),
        };
    }

    private static MihomoServiceIpcRuntimeLogEntry CreateRuntimeLogEntry(long sequence)
    {
        return new MihomoServiceIpcRuntimeLogEntry
        {
            Sequence = sequence,
            Level = MihomoServiceIpcRuntimeLogLevel.Information,
            Message = $"runtime entry {sequence}",
        };
    }
}

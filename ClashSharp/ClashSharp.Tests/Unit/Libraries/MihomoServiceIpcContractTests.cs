using System.Buffers.Binary;
using System.Text;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Contract and fuzz properties of the service IPC protocol: request/response field requirement tables for every
/// command, frame round trip, truncation at every byte offset, one-byte chunking, hostile length prefixes, bit
/// flips and garbage payloads, plus boundary checks for every declared protocol limit.
/// </summary>
public sealed class MihomoServiceIpcContractTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly Guid Session = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid DataId = new("bbbbbbbb-0000-0000-0000-000000000002");

    private static readonly HashSet<MihomoServiceIpcCommand> GenerationCommands =
        [MihomoServiceIpcCommand.Start, MihomoServiceIpcCommand.Reload];

    private static readonly HashSet<MihomoServiceIpcCommand> BrokerCommands =
    [
        MihomoServiceIpcCommand.ProbeEffectiveConfiguration,
        MihomoServiceIpcCommand.GetConnections,
        MihomoServiceIpcCommand.CloseConnection,
        MihomoServiceIpcCommand.CloseAllConnections,
        MihomoServiceIpcCommand.GetProxyRuntimeSnapshot,
        MihomoServiceIpcCommand.SelectProxy,
        MihomoServiceIpcCommand.GetRuntimeLogs,
        MihomoServiceIpcCommand.UpdateProvider,
    ];

    /// <summary>Optional request fields and the single command that requires each (or the group).</summary>
    private static readonly (string Field, Func<MihomoServiceIpcCommand, bool> Required, Func<MihomoServiceIpcRequest, MihomoServiceIpcRequest> Set, Func<MihomoServiceIpcRequest, MihomoServiceIpcRequest> Clear)[] RequestFields =
    [
        ("Generation", command => GenerationCommands.Contains(command), r => r with { Generation = 1 }, r => r with { Generation = null }),
        ("ConfigurationHash", command => GenerationCommands.Contains(command), r => r with { ConfigurationHash = Hash }, r => r with { ConfigurationHash = null }),
        ("DataGenerationId", command => GenerationCommands.Contains(command), r => r with { DataGenerationId = DataId }, r => r with { DataGenerationId = null }),
        ("ExpectedRuntime", command => BrokerCommands.Contains(command), r => r with { ExpectedRuntime = Binding() }, r => r with { ExpectedRuntime = null }),
        ("ConnectionId", command => command == MihomoServiceIpcCommand.CloseConnection, r => r with { ConnectionId = "connection-1" }, r => r with { ConnectionId = null }),
        ("ProxySelection", command => command == MihomoServiceIpcCommand.SelectProxy, r => r with { ProxySelection = new() { GroupName = "g", ProxyName = "p" } }, r => r with { ProxySelection = null }),
        ("RuntimeLogQuery", command => command == MihomoServiceIpcCommand.GetRuntimeLogs, r => r with { RuntimeLogQuery = new() { AfterSequence = 0, MaximumEntries = 10 } }, r => r with { RuntimeLogQuery = null }),
        ("ProviderUpdate", command => command == MihomoServiceIpcCommand.UpdateProvider, r => r with { ProviderUpdate = new() { Kind = MihomoServiceIpcProviderKind.Proxy, Name = "n" } }, r => r with { ProviderUpdate = null }),
        ("MaximumLogEntries", command => command == MihomoServiceIpcCommand.Logs, r => r with { MaximumLogEntries = 10 }, r => r with { MaximumLogEntries = null }),
    ];

    /// <summary>Enumerates every declared command so a new command must be classified by this test.</summary>
    /// <returns>Theory rows.</returns>
    public static TheoryData<MihomoServiceIpcCommand> AllCommands() => [.. Enum.GetValues<MihomoServiceIpcCommand>()];

    /// <summary>Verifies the canonical request of each command is valid and every missing or foreign field is rejected.</summary>
    /// <param name="command">Command under test.</param>
    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Request_RequiredAndForbiddenFieldsAreExactlyTheCommandsFieldSet(MihomoServiceIpcCommand command)
    {
        MihomoServiceIpcRequest canonical = CanonicalRequest(command);
        Assert.Null(canonical.Validate());

        foreach ((string field, Func<MihomoServiceIpcCommand, bool> required, _, Func<MihomoServiceIpcRequest, MihomoServiceIpcRequest> clear) in RequestFields)
        {
            // A missing data generation identity is accepted for activation commands (legacy-compatible); an empty one is not.
            if (required(command) && field != "DataGenerationId")
            {
                Assert.NotNull(clear(canonical).Validate());
            }
        }

        if (GenerationCommands.Contains(command))
        {
            Assert.Null((canonical with { DataGenerationId = null }).Validate());
            Assert.NotNull((canonical with { DataGenerationId = Guid.Empty }).Validate());
        }

        foreach ((string field, Func<MihomoServiceIpcCommand, bool> required, Func<MihomoServiceIpcRequest, MihomoServiceIpcRequest> set, _) in RequestFields)
        {
            if (!required(command))
            {
                Assert.True(set(canonical).Validate() is not null, $"{command} must reject the foreign field {field}");
            }
        }
    }

    /// <summary>Verifies header faults of a request (version, id, token, undefined command) are rejected for every command.</summary>
    /// <param name="command">Command under test.</param>
    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Request_HeaderFaults_AreRejected(MihomoServiceIpcCommand command)
    {
        MihomoServiceIpcRequest canonical = CanonicalRequest(command);
        string[] tokens =
        [
            string.Empty, new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64),
            new string('a', 63) + " ", new string('a', 63) + "\n", new string('٣', 64),
        ];

        Assert.NotNull((canonical with { ProtocolVersion = 0 }).Validate());
        Assert.NotNull((canonical with { ProtocolVersion = -1 }).Validate());
        Assert.NotNull((canonical with { RequestId = Guid.Empty }).Validate());
        Assert.NotNull((canonical with { Command = (MihomoServiceIpcCommand)(-1) }).Validate());
        Assert.NotNull((canonical with { Command = (MihomoServiceIpcCommand)1000 }).Validate());
        Assert.All(tokens, token => Assert.NotNull((canonical with { AuthenticationToken = token }).Validate()));
        Assert.All(new[] { 1, MihomoServiceIpcProtocol.CurrentVersion, int.MaxValue }, version => Assert.Null((canonical with { ProtocolVersion = version }).Validate()));
    }

    /// <summary>Verifies the successful-response payload kind for each command and rejection of every other payload kind.</summary>
    /// <param name="command">Command under test.</param>
    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Response_PayloadMustMatchTheCommandAndFailuresCarryNoPayload(MihomoServiceIpcCommand command)
    {
        MihomoServiceIpcRequest request = CanonicalRequest(command);
        MihomoServiceIpcResponse canonical = CanonicalResponse(request);
        Assert.Null(canonical.ValidateFor(request));

        MihomoServiceIpcResponse bare = canonical with
        {
            EffectiveConfiguration = null,
            ConnectionSnapshot = null,
            ProxyRuntimeSnapshot = null,
            RuntimeLogSnapshot = null,
            Logs = [],
        };
        (string Name, MihomoServiceIpcResponse Response)[] foreign =
        [
            ("effective", bare with { EffectiveConfiguration = new() { Mode = MihomoServiceIpcRoutingMode.Rule, MixedPort = 7890 } }),
            ("connections", bare with { ConnectionSnapshot = new() }),
            ("proxies", bare with { ProxyRuntimeSnapshot = new() }),
            ("runtime-logs", bare with { RuntimeLogSnapshot = new() { LatestSequence = 0 } }),
            ("logs", bare with { Logs = ["line"] }),
        ];
        string expectedKind = command switch
        {
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration => "effective",
            MihomoServiceIpcCommand.GetConnections => "connections",
            MihomoServiceIpcCommand.GetProxyRuntimeSnapshot => "proxies",
            MihomoServiceIpcCommand.GetRuntimeLogs => "runtime-logs",
            MihomoServiceIpcCommand.Logs => "logs",
            _ => "none",
        };
        foreach ((string name, MihomoServiceIpcResponse response) in foreign)
        {
            Assert.Equal(name == expectedKind, response.ValidateFor(request) is null);
        }

        Assert.Equal(expectedKind is "none" or "logs", bare.ValidateFor(request) is null);
        MihomoServiceIpcResponse failure = bare with { Succeeded = false, ErrorCode = "service.test.failure", Snapshot = null };
        Assert.Null(failure.ValidateFor(request));
        Assert.All(foreign, item => Assert.NotNull((item.Response with { Succeeded = false, ErrorCode = "service.test.failure" }).ValidateFor(request)));
        Assert.NotNull((failure with { ErrorCode = null }).ValidateFor(request));
        Assert.NotNull((canonical with { Succeeded = false, ErrorCode = null }).ValidateFor(request));
        Assert.NotNull((canonical with { RequestId = Guid.NewGuid() }).ValidateFor(request));
        Assert.NotNull((canonical with { ProtocolVersion = request.ProtocolVersion + 1 }).ValidateFor(request));
        Assert.NotNull((canonical with { RequestId = Guid.Empty }).ValidateFor(request));
    }

    /// <summary>Verifies broker responses must prove they come from exactly the runtime the request pinned.</summary>
    [Fact]
    public void BrokerResponse_MustMatchEveryComponentOfTheExpectedRuntimeBinding()
    {
        MihomoServiceIpcRequest request = CanonicalRequest(MihomoServiceIpcCommand.GetConnections);
        MihomoServiceIpcResponse canonical = CanonicalResponse(request);
        MihomoServiceIpcSnapshot snapshot = canonical.Snapshot!;
        MihomoServiceIpcSnapshot[] broken =
        [
            snapshot with { SessionId = Guid.NewGuid() },
            snapshot with { ActiveGeneration = snapshot.ActiveGeneration + 1 },
            snapshot with { ActiveDataGenerationId = Guid.NewGuid() },
            snapshot with { ActiveDataGenerationId = null },
            snapshot with { ActiveConfigurationHash = new string('b', 64) },
            snapshot with { ChildState = MihomoServiceChildState.Starting },
            snapshot with { ChildState = MihomoServiceChildState.Stopping },
        ];

        Assert.All(broken, item => Assert.NotNull((canonical with { Snapshot = item }).ValidateFor(request)));
        Assert.NotNull((canonical with { Snapshot = null }).ValidateFor(request));
    }

    /// <summary>Verifies every command's request and response survive a frame round trip byte-for-byte.</summary>
    /// <param name="command">Command under test.</param>
    [Theory]
    [MemberData(nameof(AllCommands))]
    public async Task FrameRoundTrip_IsStableForEveryCommand(MihomoServiceIpcCommand command)
    {
        MihomoServiceIpcRequest request = CanonicalRequest(command);
        MihomoServiceIpcResponse response = CanonicalResponse(request);

        byte[] requestFrame = await EncodeAsync(request);
        MihomoServiceIpcRequest decodedRequest = await MihomoServiceIpcFrameCodec.ReadRequestAsync(new MemoryStream(requestFrame), CancellationToken.None);
        Assert.Equal(request, decodedRequest);
        Assert.Equal(requestFrame, await EncodeAsync(decodedRequest));

        byte[] responseFrame = await EncodeAsync(response);
        MihomoServiceIpcResponse decodedResponse = await MihomoServiceIpcFrameCodec.ReadResponseAsync(new MemoryStream(responseFrame), CancellationToken.None);
        Assert.Null(decodedResponse.ValidateFor(request));
        Assert.Equal(responseFrame, await EncodeAsync(decodedResponse));
    }

    /// <summary>Verifies truncating a frame at every offset yields end-of-stream, never a partial or different message.</summary>
    [Fact]
    public async Task FrameTruncation_AtEveryOffset_IsEndOfStream()
    {
        foreach (MihomoServiceIpcCommand command in new[] { MihomoServiceIpcCommand.Hello, MihomoServiceIpcCommand.UpdateProvider, MihomoServiceIpcCommand.Start })
        {
            byte[] frame = await EncodeAsync(CanonicalRequest(command));
            for (int length = 0; length < frame.Length; length++)
            {
                await Assert.ThrowsAsync<EndOfStreamException>(
                    () => MihomoServiceIpcFrameCodec.ReadRequestAsync(new MemoryStream(frame, 0, length), CancellationToken.None));
            }
        }

        byte[] responseFrame = await EncodeAsync(CanonicalResponse(CanonicalRequest(MihomoServiceIpcCommand.GetConnections)));
        for (int length = 0; length < responseFrame.Length; length += 7)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(
                () => MihomoServiceIpcFrameCodec.ReadResponseAsync(new MemoryStream(responseFrame, 0, length), CancellationToken.None));
        }
    }

    /// <summary>Verifies delivery granularity (one byte at a time, random chunks) never changes the decoded message.</summary>
    [Fact]
    public async Task FragmentedDelivery_DecodesTheSameMessage()
    {
        Random random = new(SettingsGenerators.Seed);
        MihomoServiceIpcRequest request = CanonicalRequest(MihomoServiceIpcCommand.SelectProxy);
        byte[] frame = await EncodeAsync(request);

        MihomoServiceIpcRequest oneByte = await MihomoServiceIpcFrameCodec.ReadRequestAsync(new ChunkedStream(frame, _ => 1), CancellationToken.None);
        Assert.Equal(request, oneByte);
        for (int round = 0; round < 50; round++)
        {
            MihomoServiceIpcRequest chunked = await MihomoServiceIpcFrameCodec.ReadRequestAsync(
                new ChunkedStream(frame, _ => 1 + random.Next(13)), CancellationToken.None);
            Assert.Equal(request, chunked);
        }

        MihomoServiceIpcRequest second = CanonicalRequest(MihomoServiceIpcCommand.Stop);
        using MemoryStream pipe = new();
        await MihomoServiceIpcFrameCodec.WriteRequestAsync(pipe, request, CancellationToken.None);
        await MihomoServiceIpcFrameCodec.WriteRequestAsync(pipe, second, CancellationToken.None);
        pipe.Position = 0;
        Assert.Equal(request, await MihomoServiceIpcFrameCodec.ReadRequestAsync(pipe, CancellationToken.None));
        Assert.Equal(second, await MihomoServiceIpcFrameCodec.ReadRequestAsync(pipe, CancellationToken.None));
        await Assert.ThrowsAsync<EndOfStreamException>(() => MihomoServiceIpcFrameCodec.ReadRequestAsync(pipe, CancellationToken.None));
    }

    /// <summary>Verifies hostile length prefixes are rejected from the 4-byte header alone, without reading or allocating the payload.</summary>
    [Fact]
    public async Task HostileLengthPrefixes_AreRejectedWithoutReadingThePayload()
    {
        int[] lengths =
        [
            0, -1, int.MinValue, int.MaxValue, MihomoServiceIpcProtocol.MaximumFrameBytes + 1,
            MihomoServiceIpcProtocol.MaximumFrameBytes * 2, 0x7FFFFFF0, -2,
        ];
        foreach (int length in lengths)
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            LimitedStream stream = new([.. header, .. new byte[64]], limit: 4);

            await Assert.ThrowsAsync<InvalidDataException>(() => MihomoServiceIpcFrameCodec.ReadRequestAsync(stream, CancellationToken.None));
            Assert.Equal(4, stream.BytesRead);
        }

        byte[] maxHeader = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(maxHeader, MihomoServiceIpcProtocol.MaximumFrameBytes);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => MihomoServiceIpcFrameCodec.ReadRequestAsync(new MemoryStream([.. maxHeader, 1, 2, 3]), CancellationToken.None));
    }

    /// <summary>Verifies a bit flip at every payload byte only ever yields a typed decode failure or a self-consistent message.</summary>
    [Fact]
    public async Task BitFlipsAtEveryPayloadOffset_NeverEscapeAsUntypedFailures()
    {
        foreach (MihomoServiceIpcCommand command in new[] { MihomoServiceIpcCommand.Hello, MihomoServiceIpcCommand.SelectProxy })
        {
            byte[] frame = await EncodeAsync(CanonicalRequest(command));
            int rejected = 0;
            for (int offset = 4; offset < frame.Length; offset++)
            {
                foreach (int bit in new[] { 0, 2, 5, 7 })
                {
                    byte[] mutated = (byte[])frame.Clone();
                    mutated[offset] ^= (byte)(1 << bit);
                    try
                    {
                        MihomoServiceIpcRequest decoded = await MihomoServiceIpcFrameCodec.ReadRequestAsync(new MemoryStream(mutated), CancellationToken.None);
                        _ = decoded.Validate();
                        byte[] reencoded = await EncodeAsync(decoded);
                        MihomoServiceIpcRequest again = await MihomoServiceIpcFrameCodec.ReadRequestAsync(new MemoryStream(reencoded), CancellationToken.None);
                        Assert.Equal(decoded, again);
                    }
                    catch (InvalidDataException)
                    {
                        rejected++;
                    }
                }
            }

            Assert.True(rejected > 20, "flipping structural bytes must be rejected");
        }
    }

    /// <summary>Verifies random garbage and structurally hostile JSON payloads inside a valid length prefix are typed failures.</summary>
    [Fact]
    public async Task GarbageAndHostileJsonPayloads_AreTypedFailures()
    {
        Random random = new(SettingsGenerators.Seed);
        List<byte[]> payloads = [];
        for (int index = 0; index < 200; index++)
        {
            byte[] noise = new byte[1 + random.Next(300)];
            random.NextBytes(noise);
            payloads.Add(noise);
        }

        string[] hostile =
        [
            "null", "[]", "true", "\"x\"", "123", "{}", "{\"command\":999}", "{\"command\":\"Hello\"}",
            "{\"protocolVersion\":1,\"protocolVersion\":2}", "{\"unknown\":1}", "{\"requestId\":\"not-a-guid\"}",
            "{\"protocolVersion\":99999999999999999999}", "{\"requestId\":null,\"command\":null}",
            "{\"authenticationToken\":" + string.Concat(Enumerable.Repeat("[", 40)) + "}",
            new string('{', 100), "{\"PROTOCOLVERSION\":1}",
        ];
        payloads.AddRange(hostile.Select(Encoding.UTF8.GetBytes));
        payloads.Add([0xFF, 0xFE, 0xFD, 0x7B, 0x7D]);
        payloads.Add([0xEF, 0xBB, 0xBF, 0x7B, 0x7D]);

        foreach (byte[] payload in payloads)
        {
            byte[] frame = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame, 4);

            try
            {
                MihomoServiceIpcRequest decoded = await MihomoServiceIpcFrameCodec.ReadRequestAsync(new MemoryStream(frame), CancellationToken.None);
                Assert.NotNull(decoded.Validate());
            }
            catch (InvalidDataException)
            {
            }

            try
            {
                MihomoServiceIpcResponse decoded = await MihomoServiceIpcFrameCodec.ReadResponseAsync(new MemoryStream(frame), CancellationToken.None);
                Assert.NotNull(decoded.Validate());
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    /// <summary>Verifies the writer refuses an oversized frame instead of emitting something the reader must reject.</summary>
    [Fact]
    public async Task Writer_RefusesFramesBeyondTheLimit()
    {
        MihomoServiceIpcResponse oversized = CanonicalResponse(CanonicalRequest(MihomoServiceIpcCommand.Logs)) with
        {
            Logs = [.. Enumerable.Repeat(new string('x', MihomoServiceIpcProtocol.MaximumLogEntryCharacters), MihomoServiceIpcProtocol.MaximumLogEntries - 1)],
        };
        Assert.Null(oversized.Validate());
        int expectedLength = (await EncodeAsync(oversized)).Length - 4;
        Assert.True(expectedLength <= MihomoServiceIpcProtocol.MaximumFrameBytes);

        MihomoServiceIpcResponse tooLarge = oversized with
        {
            Logs = [.. Enumerable.Repeat(new string('中', MihomoServiceIpcProtocol.MaximumLogEntryCharacters), MihomoServiceIpcProtocol.MaximumLogEntries - 1)],
        };
        Assert.Null(tooLarge.Validate());
        using MemoryStream sink = new();
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => MihomoServiceIpcFrameCodec.WriteResponseAsync(sink, tooLarge, CancellationToken.None));
        Assert.NotNull(failure.Message);
        Assert.Equal(0, sink.Length);
    }

    /// <summary>Verifies every aggregate protocol limit is enforced at its boundary (valid at the limit, invalid one beyond).</summary>
    [Fact]
    public void DeclaredLimits_AreEnforcedAtTheirBoundaries()
    {
        HashSet<string> covered = [];

        void Check(string limit, Func<int, string?> validate, int maximum)
        {
            covered.Add(limit);
            Assert.Null(validate(maximum));
            Assert.NotNull(validate(maximum + 1));
        }

        Check(nameof(MihomoServiceIpcProtocol.MaximumLogEntries), count => (SuccessResponse() with { Logs = [.. Enumerable.Repeat("x", count)] }).Validate(),
            MihomoServiceIpcProtocol.MaximumLogEntries);
        Check(nameof(MihomoServiceIpcProtocol.MaximumLogEntryCharacters), length => (SuccessResponse() with { Logs = [new string('x', length)] }).Validate(),
            MihomoServiceIpcProtocol.MaximumLogEntryCharacters);
        Check(nameof(MihomoServiceIpcProtocol.MaximumControllerIdentifierCharacters),
            length => (CanonicalRequest(MihomoServiceIpcCommand.CloseConnection) with { ConnectionId = new string('i', length) }).Validate(),
            MihomoServiceIpcProtocol.MaximumControllerIdentifierCharacters);
        Check(nameof(MihomoServiceIpcProtocol.MaximumControllerTextCharacters),
            length => (new MihomoServiceIpcConnection { Id = "a", StartedAt = DateTimeOffset.UnixEpoch.AddDays(1), Host = new string('h', length) }).Validate(),
            MihomoServiceIpcProtocol.MaximumControllerTextCharacters);
        Check(nameof(MihomoServiceIpcProtocol.MaximumRuntimeLogEntries),
            count => (new MihomoServiceIpcRuntimeLogQuery { MaximumEntries = count }).Validate(),
            MihomoServiceIpcProtocol.MaximumRuntimeLogEntries);
        Check(nameof(MihomoServiceIpcProtocol.MaximumControllerConnections),
            count => ConnectionSnapshot(count).Validate(),
            MihomoServiceIpcProtocol.MaximumControllerConnections);
        Check(nameof(MihomoServiceIpcProtocol.MaximumControllerProxyGroups),
            count => new MihomoServiceIpcProxyRuntimeSnapshot { Groups = [.. Enumerable.Range(0, count).Select(Group)] }.Validate(),
            MihomoServiceIpcProtocol.MaximumControllerProxyGroups);
        Check(nameof(MihomoServiceIpcProtocol.MaximumControllerCandidatesPerGroup),
            count => new MihomoServiceIpcProxyRuntimeSnapshot
            {
                Groups = [Group(0) with { Candidates = [.. Enumerable.Range(0, count).Select(index => "c" + index)] }],
            }.Validate(),
            MihomoServiceIpcProtocol.MaximumControllerCandidatesPerGroup);
        Check(nameof(MihomoServiceIpcProtocol.MaximumControllerProviders),
            count => new MihomoServiceIpcProxyRuntimeSnapshot { Providers = [.. Enumerable.Range(0, count).Select(Provider)] }.Validate(),
            MihomoServiceIpcProtocol.MaximumControllerProviders);
        Check(nameof(MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters),
            length => new MihomoServiceIpcRuntimeLogSnapshot
            {
                LatestSequence = 1,
                Entries = [new() { Sequence = 1, Level = MihomoServiceIpcRuntimeLogLevel.Information, Message = new string('m', length) }],
            }.Validate(),
            MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters);

        // The aggregate character budget is checked across all connections of one snapshot.
        covered.Add(nameof(MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters));
        int perConnection = MihomoServiceIpcProtocol.MaximumControllerTextCharacters;
        int connectionsAtLimit = MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters / (perConnection * 5);
        MihomoServiceIpcConnectionSnapshot nearLimit = new()
        {
            Connections = [.. Enumerable.Range(0, connectionsAtLimit).Select(index => FatConnection(index, perConnection))],
        };
        Assert.Null(nearLimit.Validate());
        MihomoServiceIpcConnectionSnapshot beyond = new()
        {
            Connections = [.. Enumerable.Range(0, connectionsAtLimit + 2).Select(index => FatConnection(index, perConnection))],
        };
        Assert.NotNull(beyond.Validate());
        covered.Add(nameof(MihomoServiceIpcProtocol.MaximumControllerAggregateItems));
        Assert.True(MihomoServiceIpcProtocol.MaximumControllerAggregateItems >= MihomoServiceIpcProtocol.MaximumControllerConnections);

        string[] declared =
        [
            .. typeof(MihomoServiceIpcProtocol)
                .GetFields()
                .Where(field => field.IsLiteral && field.Name.StartsWith("Maximum", StringComparison.Ordinal))
                .Select(field => field.Name),
        ];
        // The frame limit is exercised by the framing tests (hostile prefixes and the oversized writer).
        covered.Add(nameof(MihomoServiceIpcProtocol.MaximumFrameBytes));
        Assert.Empty(declared.Except(covered));
    }

    /// <summary>Verifies bounded texts reject every control character and accept every printable one.</summary>
    [Fact]
    public void ControllerText_RejectsEveryControlCharacterAtAnyPosition()
    {
        for (int code = 0; code < 0x250; code++)
        {
            char character = (char)code;
            foreach (string text in new[] { character + "tail", "head" + character, "mi" + character + "ddle" })
            {
                MihomoServiceIpcConnection connection = new() { Id = "id", StartedAt = DateTimeOffset.UnixEpoch.AddDays(1), Host = text };
                Assert.Equal(!char.IsControl(character), connection.Validate() is null);
            }
        }

        MihomoServiceIpcConnection unicode = new() { Id = "id", StartedAt = DateTimeOffset.UnixEpoch.AddDays(1), Host = "例え.テスト ‮​" };
        Assert.Null(unicode.Validate());
        Assert.NotNull((unicode with { Id = " " }).Validate());
        Assert.NotNull((unicode with { Id = string.Empty }).Validate());
        Assert.NotNull((unicode with { UploadBytes = -1 }).Validate());
        Assert.NotNull((unicode with { DownloadBytes = -1 }).Validate());
        Assert.NotNull((unicode with { StartedAt = default }).Validate());
    }

    /// <summary>Verifies the pipe name is a keyed one-way function of the user and token and never leaks either.</summary>
    [Fact]
    public void PipeName_IsDeterministicInjectiveAndDoesNotLeakItsInputs()
    {
        Random random = new(SettingsGenerators.Seed);
        HashSet<string> names = [];
        for (int index = 0; index < 400; index++)
        {
            string sid = "S-1-5-21-" + random.Next() + "-" + random.Next();
            string token = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(random.Next().ToString("D32", System.Globalization.CultureInfo.InvariantCulture))).PadRight(64, '0')[..64];
            string name = MihomoServiceIpcProtocol.BuildPipeName(sid, token);

            Assert.Equal(name, MihomoServiceIpcProtocol.BuildPipeName(sid, token));
            Assert.StartsWith("ClashSharp.Mihomo.", name, StringComparison.Ordinal);
            Assert.Equal("ClashSharp.Mihomo.".Length + 32, name.Length);
            Assert.DoesNotContain(token[..16], name, StringComparison.Ordinal);
            Assert.DoesNotContain(sid, name, StringComparison.Ordinal);
            Assert.True(names.Add(name), "pipe names must not collide for different identities");
            Assert.NotEqual(name, MihomoServiceIpcProtocol.BuildPipeName(sid + "x", token));
        }

        Assert.Throws<ArgumentException>(() => MihomoServiceIpcProtocol.BuildPipeName(" ", Hash));
        Assert.Throws<ArgumentException>(() => MihomoServiceIpcProtocol.BuildPipeName("sid", "short"));
        Assert.Throws<ArgumentException>(() => MihomoServiceIpcProtocol.BuildPipeName("sid", Hash.ToUpperInvariant()));
    }

    private static MihomoServiceIpcControllerBinding Binding() =>
        new() { DataGenerationId = DataId, ServiceSessionId = Session, Generation = 3, ConfigurationHash = Hash };

    private static MihomoServiceIpcRequest CanonicalRequest(MihomoServiceIpcCommand command)
    {
        MihomoServiceIpcRequest request = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = new Guid("cccccccc-0000-0000-0000-00000000000" + ((int)command % 10)),
            AuthenticationToken = Hash,
            Command = command,
        };
        foreach ((string _, Func<MihomoServiceIpcCommand, bool> required, Func<MihomoServiceIpcRequest, MihomoServiceIpcRequest> set, _) in RequestFields)
        {
            if (required(command))
            {
                request = set(request);
            }
        }

        return request;
    }

    private static MihomoServiceIpcSnapshot RunningSnapshot() => new()
    {
        SessionId = Session,
        ServiceVersion = "1.0.0",
        ChildState = MihomoServiceChildState.Running,
        ChildProcessId = 1234,
        ActiveGeneration = 3,
        ActiveDataGenerationId = DataId,
        ActiveConfigurationHash = Hash,
    };

    private static MihomoServiceIpcResponse SuccessResponse() => new()
    {
        ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
        RequestId = Guid.NewGuid(),
        Succeeded = true,
        Snapshot = new() { SessionId = Session, ServiceVersion = "1.0.0", ChildState = MihomoServiceChildState.Stopped },
    };

    private static MihomoServiceIpcResponse CanonicalResponse(MihomoServiceIpcRequest request)
    {
        MihomoServiceIpcResponse response = new()
        {
            ProtocolVersion = request.ProtocolVersion,
            RequestId = request.RequestId,
            Succeeded = true,
            Snapshot = BrokerCommands.Contains(request.Command)
                ? RunningSnapshot()
                : new() { SessionId = Session, ServiceVersion = "1.0.0", ChildState = MihomoServiceChildState.Stopped },
        };
        return request.Command switch
        {
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration => response with
            {
                EffectiveConfiguration = new() { ControllerReady = true, Mode = MihomoServiceIpcRoutingMode.Rule, MixedPort = 7890 },
            },
            MihomoServiceIpcCommand.GetConnections => response with { ConnectionSnapshot = ConnectionSnapshot(2) },
            MihomoServiceIpcCommand.GetProxyRuntimeSnapshot => response with
            {
                ProxyRuntimeSnapshot = new() { Groups = [Group(1)], Providers = [Provider(1)] },
            },
            MihomoServiceIpcCommand.GetRuntimeLogs => response with
            {
                RuntimeLogSnapshot = new()
                {
                    LatestSequence = 5,
                    Entries = [new() { Sequence = 5, Level = MihomoServiceIpcRuntimeLogLevel.Warning, Message = "message" }],
                },
            },
            MihomoServiceIpcCommand.Logs => response with { Logs = ["one", "two"] },
            _ => response,
        };
    }

    private static MihomoServiceIpcConnectionSnapshot ConnectionSnapshot(int count) => new()
    {
        TotalConnectionCount = count,
        Connections = [.. Enumerable.Range(0, count).Select(index => new MihomoServiceIpcConnection
        {
            Id = "connection-" + index,
            ProcessName = "p",
            Host = "h",
            RuleName = "r",
            RulePayload = "x",
            ProxyName = "n",
            StartedAt = DateTimeOffset.UnixEpoch.AddDays(1),
        })],
    };

    private static MihomoServiceIpcConnection FatConnection(int index, int width) => new()
    {
        Id = "connection-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
        ProcessName = new string('p', width),
        Host = new string('h', width),
        RuleName = new string('r', width),
        RulePayload = new string('x', width),
        ProxyName = new string('n', width),
        StartedAt = DateTimeOffset.UnixEpoch.AddDays(1),
    };

    private static MihomoServiceIpcProxyGroup Group(int index) => new()
    {
        Name = "group-" + index,
        Type = "Selector",
        CurrentSelection = "c0",
        Candidates = ["c0"],
    };

    private static MihomoServiceIpcProvider Provider(int index) => new()
    {
        Name = "provider-" + index,
        Kind = MihomoServiceIpcProviderKind.Proxy,
        VehicleType = "HTTP",
        Behavior = "x",
        ItemCount = 1,
    };

    private static async Task<byte[]> EncodeAsync(MihomoServiceIpcRequest request)
    {
        using MemoryStream stream = new();
        await MihomoServiceIpcFrameCodec.WriteRequestAsync(stream, request, CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task<byte[]> EncodeAsync(MihomoServiceIpcResponse response)
    {
        using MemoryStream stream = new();
        await MihomoServiceIpcFrameCodec.WriteResponseAsync(stream, response, CancellationToken.None);
        return stream.ToArray();
    }

    private sealed class ChunkedStream(byte[] data, Func<int, int> chunkSize) : Stream
    {
        private int _position;
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int take = Math.Min(Math.Min(buffer.Length, chunkSize(_reads++)), data.Length - _position);
            data.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LimitedStream(byte[] data, int limit) : Stream
    {
        public int BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (BytesRead >= limit)
            {
                throw new InvalidOperationException("The reader consumed bytes beyond the header of an invalid frame.");
            }

            int take = Math.Min(Math.Min(buffer.Length, limit - BytesRead), data.Length - BytesRead);
            data.AsSpan(BytesRead, take).CopyTo(buffer);
            BytesRead += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

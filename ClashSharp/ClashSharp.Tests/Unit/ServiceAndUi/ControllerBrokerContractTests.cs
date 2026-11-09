using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;
using ClashSharp.Tests.Unit.MihomoService;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Contract of the controller broker: a fixed typed route allowlist, bounded and validated upstream
/// payloads, writes only for objects observed in the same exchange, and stable error classification.
/// </summary>
public sealed class ControllerBrokerContractTests
{
    private static readonly string StartTimestamp = "2026-08-04T12:00:00+08:00";

    private sealed record Rig(
        SupervisorHarness Harness,
        ScriptedControllerTransportFactory Transport,
        MihomoServiceControllerBroker Broker,
        MihomoServiceIpcSnapshot Snapshot) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Harness.DisposeAsync();

        internal MihomoServiceIpcRequest Request(
            MihomoServiceIpcCommand command,
            Func<MihomoServiceIpcRequest, MihomoServiceIpcRequest>? mutate = null)
        {
            MihomoServiceIpcRequest request = new()
            {
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                RequestId = Guid.NewGuid(),
                AuthenticationToken = MihomoServiceTestSupport.Token,
                Command = command,
                ExpectedRuntime = Harness.BindingOf(Snapshot),
            };
            return mutate is null ? request : mutate(request);
        }

        internal Task<MihomoServiceControllerBrokerResult> ExecuteAsync(MihomoServiceIpcRequest request) =>
            Broker.ExecuteAsync(request, CancellationToken.None);
    }

    private static async Task<Rig> StartRigAsync()
    {
        SupervisorHarness harness = new();
        ScriptedControllerTransportFactory transport = new();
        MihomoChildOperationResult started = await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);
        Assert.True(started.Succeeded);
        MihomoServiceControllerBroker broker = new(harness.Supervisor, transport, harness.RuntimeLogs, harness.Logs);
        return new Rig(harness, transport, broker, started.Snapshot);
    }

    private static string ConnectionsJson(int count, int hostLength = 12, string? totals = null)
    {
        StringBuilder builder = new();
        builder.Append("{\"uploadTotal\":11,\"downloadTotal\":22,");
        builder.Append(totals ?? string.Empty);
        builder.Append("\"connections\":[");
        string host = new('h', hostLength);
        for (int index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"id\":\"id-").Append(index.ToString(CultureInfo.InvariantCulture)).Append("\",");
            builder.Append("\"metadata\":{\"host\":\"").Append(host).Append("\",\"process\":\"C:\\\\app\\\\client.exe\"},");
            builder.Append("\"rule\":\"MATCH\",\"rulePayload\":\"\",\"chains\":[\"DIRECT\"],\"upload\":1,\"download\":2,");
            builder.Append("\"start\":\"").Append(StartTimestamp).Append("\"}");
        }

        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>Gets a size matrix around the detail and frame budgets.</summary>
    public static TheoryData<int, int> ConnectionSizes
    {
        get
        {
            TheoryData<int, int> data = [];
            foreach (int count in new[] { 0, 1, 2047, 2048, 2049, 4096 })
            {
                foreach (int hostLength in new[] { 12, 3000 })
                {
                    data.Add(count, hostLength);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ConnectionSizes))]
    public async Task Connections_AlwaysYieldAValidSnapshotThatFitsOneFrame_AndKeepsTheTrueCount(
        int count,
        int hostLength)
    {
        await using Rig rig = await StartRigAsync();
        rig.Transport.Handler = _ => ScriptedControllerTransportFactory.Json(ConnectionsJson(count, hostLength));

        MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
            rig.Request(MihomoServiceIpcCommand.GetConnections));

        Assert.True(result.Succeeded, result.ErrorCode);
        MihomoServiceIpcConnectionSnapshot snapshot = result.Payload!.ConnectionSnapshot!;
        Assert.Null(snapshot.Validate());
        Assert.Equal(count, snapshot.TotalConnectionCount);
        Assert.Equal(11, snapshot.UploadTotalBytes);
        Assert.Equal(22, snapshot.DownloadTotalBytes);
        if (snapshot.ConnectionsComplete)
        {
            Assert.Equal(count, snapshot.Connections.Count);
            Assert.True(count <= MihomoServiceIpcProtocol.MaximumControllerConnections);
        }
        else
        {
            Assert.Empty(snapshot.Connections);
        }

        MihomoServiceIpcResponse response = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            Succeeded = true,
            Snapshot = result.Snapshot,
            ConnectionSnapshot = snapshot,
        };
        using MemoryStream stream = new();
        await MihomoServiceIpcFrameCodec.WriteResponseAsync(stream, response, CancellationToken.None);
        Assert.True(stream.Length <= MihomoServiceIpcProtocol.MaximumFrameBytes + sizeof(int));
        Assert.Equal(
            [(HttpMethod.Get, "/connections")],
            rig.Transport.Calls.Select(call => (call.Method, call.Path)).ToArray());
    }

    /// <summary>Gets named hostile connection documents.</summary>
    public static TheoryData<string> HostileConnectionDocuments
    {
        get
        {
            TheoryData<string> data = [];
            foreach (string name in HostileDocuments().Keys)
            {
                data.Add(name);
            }

            return data;
        }
    }

    private static Dictionary<string, string> HostileDocuments()
    {
        string Row(string id = "\"id-1\"", string start = "\"2026-08-04T12:00:00+08:00\"", string chains = "[\"DIRECT\"]", string extra = "") =>
            $"{{\"id\":{id},\"metadata\":{{\"host\":\"h\"}},\"rule\":\"MATCH\",\"chains\":{chains},\"upload\":1,\"download\":2,\"start\":{start}{extra}}}";
        string Doc(string rows, string totals = "\"uploadTotal\":1,\"downloadTotal\":2,") =>
            $"{{{totals}\"connections\":[{rows}]}}";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["row-without-id"] = Doc("{\"metadata\":{},\"start\":\"2026-08-04T12:00:00+08:00\"}"),
            ["row-id-empty"] = Doc(Row("\"\"")),
            ["row-id-whitespace"] = Doc(Row("\"   \"")),
            ["row-id-control-char"] = Doc(Row("\"a\\u0007b\"")),
            ["row-id-lone-high-surrogate"] = Doc(Row("\"a\\ud800b\"")),
            ["row-id-lone-low-surrogate"] = Doc(Row("\"a\\udc00b\"")),
            ["row-id-too-long"] = Doc(Row("\"" + new string('i', 513) + "\"")),
            ["row-id-not-a-string"] = Doc(Row("123")),
            ["row-start-invalid"] = Doc(Row(start: "\"not a time\"")),
            ["row-start-missing"] = Doc("{\"id\":\"x\",\"metadata\":{}}"),
            ["row-start-zero-time"] = Doc(Row(start: "\"0001-01-01T00:00:00Z\"")),
            ["rows-with-duplicate-ids"] = Doc(Row("\"same\"") + "," + Row("\"same\"")),
            ["row-start-number"] = Doc(Row(start: "1700000000")),
            ["row-chains-not-array"] = Doc(Row(chains: "\"DIRECT\"")),
            ["row-chains-too-long"] = Doc(Row(chains: "[" + string.Join(",", Enumerable.Repeat("\"x\"", 65)) + "]")),
            ["row-duplicate-property"] = Doc(Row(extra: ",\"id\":\"dup\"")),
            ["row-not-an-object"] = Doc("123"),
            ["row-null"] = Doc("null"),
            ["connections-is-an-object"] = "{\"uploadTotal\":1,\"downloadTotal\":2,\"connections\":{}}",
            ["connections-is-a-string"] = "{\"uploadTotal\":1,\"downloadTotal\":2,\"connections\":\"x\"}",
            ["connections-missing"] = "{\"uploadTotal\":1,\"downloadTotal\":2}",
            ["upload-total-negative"] = Doc(Row(), "\"uploadTotal\":-1,\"downloadTotal\":2,"),
            ["upload-total-fractional"] = Doc(Row(), "\"uploadTotal\":1.5,\"downloadTotal\":2,"),
            ["upload-total-string"] = Doc(Row(), "\"uploadTotal\":\"1\",\"downloadTotal\":2,"),
            ["download-total-overflow"] = Doc(Row(), "\"uploadTotal\":1,\"downloadTotal\":18446744073709551616,"),
            ["download-total-missing"] = Doc(Row(), "\"uploadTotal\":1,"),
            ["root-is-array"] = "[]",
            ["root-is-null"] = "null",
            ["root-is-string"] = "\"x\"",
            ["empty-body"] = string.Empty,
            ["whitespace-body"] = "   ",
            ["truncated-json"] = "{\"uploadTotal\":1,\"downloadTotal\":2,\"connections\":[",
            ["trailing-garbage"] = Doc(Row()) + "x",
            ["comment-in-json"] = "{/*c*/\"uploadTotal\":1,\"downloadTotal\":2,\"connections\":[]}",
            ["trailing-comma"] = "{\"uploadTotal\":1,\"downloadTotal\":2,\"connections\":[],}",
            ["duplicate-root-total"] = "{\"uploadTotal\":1,\"uploadTotal\":2,\"downloadTotal\":3,\"connections\":[]}",
            ["nesting-beyond-parser-depth"] = "{\"uploadTotal\":1,\"downloadTotal\":2,\"connections\":[],\"x\":" + new string('[', 80) + new string(']', 80) + "}",
        };
    }

    [Theory]
    [MemberData(nameof(HostileConnectionDocuments))]
    public async Task HostileConnectionDocument_IsRejectedAsInvalidUpstreamAndNeverEscapesAsAnException(string name)
    {
        await using Rig rig = await StartRigAsync();
        rig.Transport.Handler = _ => ScriptedControllerTransportFactory.Json(HostileDocuments()[name]);

        MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
            rig.Request(MihomoServiceIpcCommand.GetConnections));

        Assert.False(result.Succeeded, name);
        Assert.Equal("service.controller.upstream_invalid", result.ErrorCode);
        Assert.Null(result.Payload);
        Assert.Equal(MihomoServiceChildState.Running, result.Snapshot.ChildState);
        Assert.DoesNotContain(
            rig.Harness.Logs.ReadLatest(256),
            line => line.Contains("not a time", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectionText_IsAlwaysSingleLineBoundedAndWellFormed_ForGeneratedHostileText()
    {
        Random random = new(20261109);
        await using Rig rig = await StartRigAsync();
        UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        for (int round = 0; round < 40; round++)
        {
            string host = RandomHostileText(random, random.Next(0, 6000));
            string rule = RandomHostileText(random, random.Next(0, 6000));
            string json = JsonSerializer.Serialize(new
            {
                uploadTotal = 1,
                downloadTotal = 2,
                connections = new[]
                {
                    new
                    {
                        id = "id-" + round,
                        metadata = new { host, process = RandomHostileText(random, 30) },
                        rule,
                        rulePayload = RandomHostileText(random, 50),
                        chains = new[] { RandomHostileText(random, 20) },
                        upload = 1,
                        download = 2,
                        start = StartTimestamp,
                    },
                },
            });
            rig.Transport.Handler = _ => ScriptedControllerTransportFactory.Json(json);

            MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
                rig.Request(MihomoServiceIpcCommand.GetConnections));

            Assert.True(result.Succeeded, $"round {round}: {result.ErrorCode}");
            MihomoServiceIpcConnection connection = Assert.Single(result.Payload!.ConnectionSnapshot!.Connections);
            foreach (string text in new[] { connection.Host, connection.RuleName, connection.RulePayload, connection.ProxyName, connection.ProcessName })
            {
                Assert.True(text.Length <= MihomoServiceIpcProtocol.MaximumControllerTextCharacters);
                Assert.DoesNotContain(text, char.IsControl);
                _ = strict.GetBytes(text);
                Assert.Equal(text.Trim(), text);
            }

            Assert.Null(result.Payload.ConnectionSnapshot.Validate());
        }
    }

    [Fact]
    public async Task ProcessName_NeverContainsADirectory()
    {
        await using Rig rig = await StartRigAsync();
        string[] candidates =
        [
            "C:\\\\Users\\\\someone\\\\app.exe", "D:/dir/with space/tool.exe", "\\\\\\\\server\\\\share\\\\x.exe",
            "plain.exe", "dir\\\\", "../../etc/passwd", "app.exe\\\\",
        ];
        foreach (string candidate in candidates)
        {
            string json = "{\"uploadTotal\":1,\"downloadTotal\":2,\"connections\":[{\"id\":\"x\",\"metadata\":{\"processPath\":\""
                + candidate + "\",\"host\":\"h\"},\"rule\":\"MATCH\",\"chains\":[\"DIRECT\"],\"upload\":0,\"download\":0,\"start\":\""
                + StartTimestamp + "\"}]}";
            rig.Transport.Handler = _ => ScriptedControllerTransportFactory.Json(json);

            MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
                rig.Request(MihomoServiceIpcCommand.GetConnections));

            Assert.True(result.Succeeded, candidate);
            string processName = Assert.Single(result.Payload!.ConnectionSnapshot!.Connections).ProcessName;
            Assert.DoesNotContain('/', processName);
            Assert.DoesNotContain('\\', processName);
            Assert.False(string.IsNullOrWhiteSpace(processName));
        }
    }

    [Fact]
    public async Task ProxyRuntime_RejectsEverySizeOrShapeViolation_AndAcceptsTheLimits()
    {
        await using Rig rig = await StartRigAsync();

        string Groups(int groupCount, int candidates) => "{\"proxies\":{"
            + string.Join(",", Enumerable.Range(0, groupCount).Select(group =>
                $"\"g{group}\":{{\"name\":\"g{group}\",\"type\":\"Selector\",\"now\":\"c0\",\"all\":["
                + string.Join(",", Enumerable.Range(0, candidates).Select(candidate => $"\"c{candidate}\"")) + "]}"))
            + "}}";
        string Providers(int count) => "{\"providers\":{"
            + string.Join(",", Enumerable.Range(0, count).Select(index => $"\"p{index}\":{{\"name\":\"p{index}\",\"vehicleType\":\"HTTP\",\"proxies\":[]}}"))
            + "}}";
        (string Name, string Groups, string Providers, bool Accepted)[] cases =
        [
            ("minimal", Groups(1, 1), Providers(0), true),
            ("max-groups", Groups(MihomoServiceIpcProtocol.MaximumControllerProxyGroups, 1), Providers(0), true),
            ("too-many-groups", Groups(MihomoServiceIpcProtocol.MaximumControllerProxyGroups + 1, 1), Providers(0), false),
            ("max-candidates", Groups(1, MihomoServiceIpcProtocol.MaximumControllerCandidatesPerGroup), Providers(0), true),
            ("too-many-candidates", Groups(1, MihomoServiceIpcProtocol.MaximumControllerCandidatesPerGroup + 1), Providers(0), false),
            ("aggregate-too-large", Groups(5, MihomoServiceIpcProtocol.MaximumControllerCandidatesPerGroup), Providers(0), false),
            ("max-providers", Groups(1, 1), Providers(MihomoServiceIpcProtocol.MaximumControllerProviders / 2), true),
            ("too-many-providers", Groups(1, 1), Providers(MihomoServiceIpcProtocol.MaximumControllerProviders + 1), false),
            ("empty-candidate-list", "{\"proxies\":{\"g\":{\"all\":[]}}}", Providers(0), false),
            ("candidate-not-a-string", "{\"proxies\":{\"g\":{\"all\":[1]}}}", Providers(0), false),
            ("candidate-empty", "{\"proxies\":{\"g\":{\"all\":[\"\"]}}}", Providers(0), false),
            ("candidate-control", "{\"proxies\":{\"g\":{\"all\":[\"a\\u0001\"]}}}", Providers(0), false),
            ("group-name-too-long", "{\"proxies\":{\"" + new string('g', 513) + "\":{\"all\":[\"a\"]}}}", Providers(0), false),
            ("all-not-an-array", "{\"proxies\":{\"g\":{\"all\":\"a\"}}}", Providers(0), false),
            ("proxies-not-an-object", "{\"proxies\":[]}", Providers(0), false),
            ("duplicate-group-key", "{\"proxies\":{\"g\":{\"all\":[\"a\"]},\"g\":{\"all\":[\"b\"]}}}", Providers(0), false),
            ("provider-not-an-object", Groups(1, 1), "{\"providers\":{\"p\":1}}", false),
            ("providers-not-an-object", Groups(1, 1), "{\"providers\":[]}", false),
            ("provider-name-control", Groups(1, 1), "{\"providers\":{\"p\\u0002\":{}}}", false),
        ];

        foreach ((string name, string groups, string providers, bool accepted) in cases)
        {
            rig.Transport.Handler = call => call.Path switch
            {
                "/proxies" => ScriptedControllerTransportFactory.Json(groups),
                "/providers/proxies" => ScriptedControllerTransportFactory.Json(providers),
                "/providers/rules" => ScriptedControllerTransportFactory.Json("{\"providers\":{}}"),
                _ => throw new InvalidOperationException("unexpected route " + call.Path),
            };

            MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
                rig.Request(MihomoServiceIpcCommand.GetProxyRuntimeSnapshot));

            Assert.True(accepted == result.Succeeded, $"{name}: {result.ErrorCode}");
            if (accepted)
            {
                Assert.Null(result.Payload!.ProxyRuntimeSnapshot!.Validate());
            }
            else
            {
                Assert.Equal("service.controller.upstream_invalid", result.ErrorCode);
            }
        }
    }

    [Fact]
    public async Task ProxyGroups_DuplicateCandidatesCollapseAndAnUnknownCurrentSelectionFallsBackToTheFirst()
    {
        await using Rig rig = await StartRigAsync();
        rig.Transport.Handler = call => call.Path switch
        {
            "/proxies" => ScriptedControllerTransportFactory.Json(
                "{\"proxies\":{\"g\":{\"name\":\"g\",\"type\":\"Selector\",\"now\":\"ghost\",\"all\":[\"a\",\"b\",\"a\",\"b\",\"c\"]}}}"),
            _ => ScriptedControllerTransportFactory.Json("{\"providers\":{}}"),
        };

        MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
            rig.Request(MihomoServiceIpcCommand.GetProxyRuntimeSnapshot));

        MihomoServiceIpcProxyGroup group = Assert.Single(result.Payload!.ProxyRuntimeSnapshot!.Groups);
        Assert.Equal(["a", "b", "c"], group.Candidates);
        Assert.Equal("a", group.CurrentSelection);
        Assert.Contains(group.CurrentSelection, group.Candidates);
    }

    [Fact]
    public async Task SelectProxy_WritesOnlyWhenTheGroupAndCandidateWereObservedInTheSameExchange()
    {
        await using Rig rig = await StartRigAsync();
        string[] groups = ["Proxy", "规则 组", "a/b", "g?x", "g#y", "%41", "dot.name"];
        string[] candidates = ["n1", "n 2", "节点", "x/y", "n?z", "😀"];
        rig.Transport.Handler = call =>
        {
            if (call.Method == HttpMethod.Get && call.Path == "/proxies")
            {
                return ScriptedControllerTransportFactory.Json(JsonSerializer.Serialize(new
                {
                    proxies = groups.ToDictionary(
                        group => group,
                        group => new { name = group, type = "Selector", now = candidates[0], all = candidates }),
                }));
            }

            return new MihomoControllerHttpResponse(HttpStatusCode.NoContent, ReadOnlyMemory<byte>.Empty);
        };
        Random random = new(20261109);

        for (int round = 0; round < 60; round++)
        {
            bool validGroup = random.Next(3) != 0;
            bool validCandidate = random.Next(3) != 0;
            string group = validGroup ? groups[random.Next(groups.Length)] : "ghost-" + random.Next(10);
            string proxy = validCandidate ? candidates[random.Next(candidates.Length)] : "ghost-" + random.Next(10);
            int before = rig.Transport.Calls.Length;

            MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
                rig.Request(MihomoServiceIpcCommand.SelectProxy, request => request with
                {
                    ProxySelection = new MihomoServiceIpcProxySelection { GroupName = group, ProxyName = proxy },
                }));

            RecordedControllerCall[] calls = rig.Transport.Calls[before..];
            Assert.Equal(HttpMethod.Get, calls[0].Method);
            RecordedControllerCall[] writes = [.. calls.Where(call => call.Method != HttpMethod.Get)];
            if (validGroup && validCandidate)
            {
                Assert.True(result.Succeeded, $"{group}/{proxy}: {result.ErrorCode}");
                RecordedControllerCall write = Assert.Single(writes);
                Assert.Equal(HttpMethod.Put, write.Method);
                Assert.Equal("/proxies/" + Uri.EscapeDataString(group), write.Path);
                using JsonDocument body = JsonDocument.Parse(write.Body);
                Assert.Equal(proxy, body.RootElement.GetProperty("name").GetString());
                Assert.Single(body.RootElement.EnumerateObject());
            }
            else
            {
                Assert.False(result.Succeeded);
                Assert.Equal("service.controller.proxy_selection_invalid", result.ErrorCode);
                Assert.Empty(writes);
            }
        }
    }

    [Fact]
    public async Task EveryBrokerCommand_UsesOnlyItsFixedRoutesWithBoundedResponseLimits()
    {
        await using Rig rig = await StartRigAsync();
        rig.Transport.Handler = call => call.Path switch
        {
            "/connections" when call.Method == HttpMethod.Get => ScriptedControllerTransportFactory.Json(ConnectionsJson(1)),
            "/proxies" => ScriptedControllerTransportFactory.Json(
                "{\"proxies\":{\"g\":{\"name\":\"g\",\"type\":\"Selector\",\"now\":\"a\",\"all\":[\"a\"]}}}"),
            "/providers/proxies" or "/providers/rules" => ScriptedControllerTransportFactory.Json("{\"providers\":{}}"),
            _ => new MihomoControllerHttpResponse(HttpStatusCode.NoContent, ReadOnlyMemory<byte>.Empty),
        };
        Dictionary<MihomoServiceIpcCommand, string[]> allowedRoutes = new()
        {
            [MihomoServiceIpcCommand.ProbeEffectiveConfiguration] = [],
            [MihomoServiceIpcCommand.GetRuntimeLogs] = [],
            [MihomoServiceIpcCommand.GetConnections] = ["GET /connections"],
            [MihomoServiceIpcCommand.CloseConnection] = ["DELETE /connections/connection-1"],
            [MihomoServiceIpcCommand.CloseAllConnections] = ["DELETE /connections"],
            [MihomoServiceIpcCommand.GetProxyRuntimeSnapshot] = ["GET /proxies", "GET /providers/proxies", "GET /providers/rules"],
            [MihomoServiceIpcCommand.SelectProxy] = ["GET /proxies", "PUT /proxies/g"],
            [MihomoServiceIpcCommand.UpdateProvider] = ["PUT /providers/proxies/provider"],
        };

        foreach ((MihomoServiceIpcCommand command, string[] routes) in allowedRoutes)
        {
            int before = rig.Transport.Calls.Length;
            MihomoServiceIpcRequest request = rig.Request(command, shaped =>
            {
                MihomoServiceIpcRequest basis = CommandProcessorContractTests.Shape(command, shaped.ExpectedRuntime!.ConfigurationHash);
                return basis with
                {
                    RequestId = shaped.RequestId,
                    ExpectedRuntime = shaped.ExpectedRuntime,
                    ProxySelection = basis.ProxySelection is null
                        ? null
                        : new MihomoServiceIpcProxySelection { GroupName = "g", ProxyName = "a" },
                };
            });

            MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(request);

            Assert.True(result.Succeeded, $"{command}: {result.ErrorCode}");
            RecordedControllerCall[] calls = rig.Transport.Calls[before..];
            Assert.Equal(routes, calls.Select(call => $"{call.Method} {call.Path}").ToArray());
            Assert.All(calls, call =>
            {
                Assert.InRange(call.MaximumResponseBytes, 1, 8 * 1024 * 1024);
                if (call.Method != HttpMethod.Get)
                {
                    Assert.True(call.MaximumResponseBytes <= 8 * 1024, "A mutating call accepts an unbounded body.");
                }
            });
        }

        // Every enum member must be classified: unknown controller commands are not silently routed.
        Assert.All(
            Enum.GetValues<MihomoServiceIpcCommand>().Where(command => command is not (
                MihomoServiceIpcCommand.Hello or MihomoServiceIpcCommand.Status or MihomoServiceIpcCommand.Start
                or MihomoServiceIpcCommand.Reload or MihomoServiceIpcCommand.Stop or MihomoServiceIpcCommand.Logs)),
            command => Assert.True(allowedRoutes.ContainsKey(command), $"{command} has no fixed-route contract."));
    }

    [Fact]
    public async Task LifecycleCommandsAreNeverRoutedToTheController()
    {
        await using Rig rig = await StartRigAsync();
        MihomoServiceIpcCommand[] lifecycle =
        [
            MihomoServiceIpcCommand.Hello,
            MihomoServiceIpcCommand.Status,
            MihomoServiceIpcCommand.Start,
            MihomoServiceIpcCommand.Reload,
            MihomoServiceIpcCommand.Stop,
            MihomoServiceIpcCommand.Logs,
        ];

        foreach (MihomoServiceIpcCommand command in lifecycle)
        {
            MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(rig.Request(command));

            Assert.False(result.Succeeded);
            Assert.Equal(MihomoServiceChildState.Running, result.Snapshot.ChildState);
        }

        Assert.Empty(rig.Transport.Calls);
    }

    /// <summary>Gets exception and status classes with the code the broker must report.</summary>
    public static TheoryData<string, string> FailureClasses => new()
    {
        { "http-request", "service.controller.request_failed" },
        { "io", "service.controller.request_failed" },
        { "unauthorized-access", "service.controller.request_failed" },
        { "timeout", "service.controller.request_failed" },
        { "invalid-operation", "service.controller.request_failed" },
        { "identity", "service.controller.server_identity_invalid" },
        { "identity-wrapped", "service.controller.server_identity_invalid" },
        { "json", "service.controller.upstream_invalid" },
        { "format", "service.controller.upstream_invalid" },
        { "invalid-data", "service.controller.upstream_invalid" },
        { "typed-upstream", "service.controller.custom_code" },
        { "status-500", "service.controller.upstream_status" },
        { "status-401", "service.controller.upstream_status" },
        { "status-404", "service.controller.upstream_status" },
        { "status-301", "service.controller.upstream_status" },
    };

    [Theory]
    [MemberData(nameof(FailureClasses))]
    public async Task TransportFailure_IsClassifiedIntoAStableCode_AndNeverLeaksTheExceptionMessage(
        string failure,
        string expectedCode)
    {
        await using Rig rig = await StartRigAsync();
        const string Leak = "LEAK-pipe-secret-0123456789";
        rig.Transport.Handler = _ => failure switch
        {
            "http-request" => throw new HttpRequestException(Leak),
            "io" => throw new IOException(Leak),
            "unauthorized-access" => throw new UnauthorizedAccessException(Leak),
            "timeout" => throw new TimeoutException(Leak),
            "invalid-operation" => throw new InvalidOperationException(Leak),
            "identity" => throw new MihomoControllerServerIdentityException(),
            "identity-wrapped" => throw new HttpRequestException(Leak, new MihomoControllerServerIdentityException()),
            "json" => throw new JsonException(Leak),
            "format" => throw new FormatException(Leak),
            "invalid-data" => throw new InvalidDataException(Leak),
            "typed-upstream" => throw new MihomoControllerUpstreamException("service.controller.custom_code"),
            _ => new MihomoControllerHttpResponse(
                (HttpStatusCode)int.Parse(failure["status-".Length..], CultureInfo.InvariantCulture),
                Encoding.UTF8.GetBytes(Leak)),
        };

        MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
            rig.Request(MihomoServiceIpcCommand.GetConnections));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Null(result.Payload);
        Assert.Equal(MihomoServiceChildState.Running, result.Snapshot.ChildState);
        Assert.DoesNotContain(
            rig.Harness.Logs.ReadLatest(256),
            line => line.Contains(Leak, StringComparison.Ordinal));
        MihomoServiceIpcResponse response = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            Succeeded = false,
            ErrorCode = result.ErrorCode,
            Snapshot = result.Snapshot,
        };
        Assert.Null(response.Validate());
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAndReleasesTheCommandGate()
    {
        await using Rig rig = await StartRigAsync();
        using CancellationTokenSource caller = new();
        rig.Transport.Handler = _ =>
        {
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Broker.ExecuteAsync(
            rig.Request(MihomoServiceIpcCommand.GetConnections),
            caller.Token));

        // The command gate was released: a lifecycle command still completes.
        Assert.True((await rig.Harness.Supervisor.StartAsync(1, rig.Harness.Hash, CancellationToken.None)
            .WaitAsync(TestTimeouts.Completion)).Succeeded);
    }

    [Fact]
    public async Task ABindingThatWentStaleDuringTheUpstreamCall_DiscardsTheValue()
    {
        await using Rig rig = await StartRigAsync();
        rig.Transport.Handler = _ =>
        {
            // The child is replaced while the controller request is still in flight.
            rig.Harness.Launcher.Snapshot()[^1].Exit(1);
            SpinWait.SpinUntil(() => rig.Harness.Supervisor.GetSnapshot().ChildState != MihomoServiceChildState.Running
                || rig.Harness.Launcher.Attempts > 1, TimeSpan.FromSeconds(5));
            return ScriptedControllerTransportFactory.Json(ConnectionsJson(1));
        };

        MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
            rig.Request(MihomoServiceIpcCommand.GetConnections));

        Assert.False(result.Succeeded);
        Assert.Equal("service.controller.stale_generation", result.ErrorCode);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task BrokerAnswersWithoutAnExpectedRuntimeAreRefusedWithoutTouchingTheController()
    {
        await using Rig rig = await StartRigAsync();

        foreach (MihomoServiceIpcCommand command in new[]
                 {
                     MihomoServiceIpcCommand.GetConnections,
                     MihomoServiceIpcCommand.CloseAllConnections,
                     MihomoServiceIpcCommand.GetProxyRuntimeSnapshot,
                 })
        {
            MihomoServiceControllerBrokerResult result = await rig.ExecuteAsync(
                rig.Request(command, request => request with { ExpectedRuntime = null }));

            Assert.Equal("service.controller.binding_invalid", result.ErrorCode);
        }

        Assert.Empty(rig.Transport.Calls);
    }

    /// <summary>Gets identifiers that are legal for the broker and awkward for URLs.</summary>
    public static TheoryData<string> AwkwardIdentifiers => new()
    {
        "plain",
        "with space",
        "a/b",
        "a?b",
        "a#b",
        "a%2fb",
        "%2e%2e",
        "..%2f..",
        "ü",
        "😀",
        "a;b",
        "a:b",
        "@",
        "*",
        "a+b",
        "a\\b",
        ".",
        "..",
        "...",
    };

    [Theory]
    [MemberData(nameof(AwkwardIdentifiers))]
    public async Task IdentifierBearingRoutes_PutExactlyOneEscapedSegmentOnTheWire(string identifier)
    {
        await using Rig rig = await StartRigAsync();
        await using FakeControllerPipeServer controller = new();
        MihomoServiceControllerBroker broker = new(
            rig.Harness.Supervisor,
            new RealPipeTransportFactory(controller.PipeName),
            rig.Harness.RuntimeLogs,
            rig.Harness.Logs);
        (MihomoServiceIpcCommand Command, string Prefix, string Method)[] routes =
        [
            (MihomoServiceIpcCommand.CloseConnection, "/connections/", "DELETE"),
            (MihomoServiceIpcCommand.UpdateProvider, "/providers/proxies/", "PUT"),
        ];

        foreach ((MihomoServiceIpcCommand command, string prefix, string method) in routes)
        {
            int before = controller.RequestLines.Length;
            MihomoServiceIpcRequest request = rig.Request(command, shaped => command == MihomoServiceIpcCommand.CloseConnection
                ? shaped with { ConnectionId = identifier }
                : shaped with { ProviderUpdate = new MihomoServiceIpcProviderUpdate { Kind = MihomoServiceIpcProviderKind.Proxy, Name = identifier } });

            MihomoServiceControllerBrokerResult result = await broker.ExecuteAsync(request, CancellationToken.None);

            string[] lines = controller.RequestLines[before..];
            if (!result.Succeeded)
            {
                // Refusing an identifier is always acceptable; sending a different route is not.
                Assert.Empty(lines);
                continue;
            }

            string line = Assert.Single(lines);
            Assert.Equal($"{method} {prefix}{Uri.EscapeDataString(identifier)} HTTP/1.1", line);
        }
    }

    private static string RandomHostileText(Random random, int length)
    {
        string[] pieces =
        [
            "a", "b", "Z", "0", " ", "\t", "\n", "\r", "\u0001", "\u007f", "\u0085", "\u2028", "é", "中", "😀",
            "\u200b", "\ufeff", "x\\y", "\"", "'",
        ];
        StringBuilder builder = new();
        while (builder.Length < length)
        {
            builder.Append(pieces[random.Next(pieces.Length)]);
        }

        return builder.ToString();
    }
}

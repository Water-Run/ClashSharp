using System.Text;
using System.Text.Json;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Contract: whatever one connection sends, the single serial pipe loop must survive and keep serving
/// the next well-formed request. A service whose IPC loop dies on one hostile frame is a denial of
/// service for every later Clash# command and, under the generic host, stops the whole service.
/// </summary>
public sealed class PipeServerResilienceTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, Func<string, byte[]>> HostileFrames = BuildCorpus();

    /// <summary>Gets the name of every hostile byte sequence in the corpus.</summary>
    public static TheoryData<string> HostileFrameNames
    {
        get
        {
            TheoryData<string> data = [];
            foreach (string name in HostileFrames.Keys)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HostileFrameNames))]
    public async Task HostileFrame_NeverTerminatesTheServerLoop(string name)
    {
        if (!PipeServerHarness.CanRun())
        {
            return;
        }

        await using PipeServerHarness harness = PipeServerHarness.Start();

        await harness.SendRawAndCloseAsync(HostileFrames[name](harness.Token));

        await AssertServerStillServesAsync(harness);
    }

    [Fact]
    public async Task HostileFrames_InterleavedWithValidRequests_NeverDisturbValidResponses()
    {
        if (!PipeServerHarness.CanRun())
        {
            return;
        }

        await using PipeServerHarness harness = PipeServerHarness.Start();
        Random random = new(20261109);
        string[] names = [.. HostileFrames.Keys];

        for (int round = 0; round < 25; round++)
        {
            string name = names[random.Next(names.Length)];
            await harness.SendRawAndCloseAsync(HostileFrames[name](harness.Token));
            await AssertServerStillServesAsync(harness);
        }
    }

    [Fact]
    public async Task StoppingWhileAClientHoldsAnIdleConnection_CompletesWithoutWaitingForTheFrameTimeout()
    {
        if (!PipeServerHarness.CanRun())
        {
            return;
        }

        PipeServerHarness harness = PipeServerHarness.Start();
        await using System.IO.Pipes.NamedPipeClientStream idle = await harness.ConnectAsync();

        // The loop is now blocked reading a frame header that never arrives. Disposal cancels the
        // stopping token and must complete well inside the five-second whole-connection deadline.
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await harness.DisposeAsync();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < MihomoServicePipeServer.ConnectionTimeout,
            "Service shutdown waited for the per-connection timeout instead of cancelling the read.");
        Assert.True(harness.ServerTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RepeatedAndReorderedLifecycleRequests_AreSerializedAndStayCoherent()
    {
        if (!PipeServerHarness.CanRun())
        {
            return;
        }

        await using PipeServerHarness harness = PipeServerHarness.Start();
        Random random = new(20261109);
        MihomoServiceIpcCommand[] commands =
        [
            MihomoServiceIpcCommand.Hello,
            MihomoServiceIpcCommand.Status,
            MihomoServiceIpcCommand.Stop,
            MihomoServiceIpcCommand.Logs,
        ];
        Guid? session = null;

        for (int index = 0; index < 40; index++)
        {
            MihomoServiceIpcCommand command = commands[random.Next(commands.Length)];
            MihomoServiceIpcRequest request = harness.NewRequest(command) with
            {
                MaximumLogEntries = command == MihomoServiceIpcCommand.Logs ? random.Next(1, 20) : null,
            };

            MihomoServiceIpcResponse response = await harness.ExchangeAsync(request);

            Assert.True(response.Succeeded, $"{command} failed with {response.ErrorCode}.");
            Assert.Null(response.ValidateFor(request));
            Assert.Equal(MihomoServiceChildState.Stopped, response.Snapshot!.ChildState);
            session ??= response.Snapshot.SessionId;
            Assert.Equal(session, response.Snapshot.SessionId);
        }
    }

    [Fact]
    public async Task ResponseForTheSameRequestIdIsStableWhenTheRequestIsRepeated()
    {
        if (!PipeServerHarness.CanRun())
        {
            return;
        }

        await using PipeServerHarness harness = PipeServerHarness.Start();
        MihomoServiceIpcRequest request = harness.NewRequest(MihomoServiceIpcCommand.Status);

        MihomoServiceIpcResponse first = await harness.ExchangeAsync(request);
        MihomoServiceIpcResponse second = await harness.ExchangeAsync(request);

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(first.Snapshot, second.Snapshot);
        Assert.Equal(first.Succeeded, second.Succeeded);
    }

    private static async Task AssertServerStillServesAsync(PipeServerHarness harness)
    {
        MihomoServiceIpcRequest hello = harness.NewRequest();
        Task<MihomoServiceIpcResponse> exchange = harness.ExchangeAsync(hello);
        Task finished = await Task.WhenAny(exchange, harness.ServerTask);
        if (finished == harness.ServerTask)
        {
            // Surfaces the exception that killed the loop, or fails if it ended silently.
            await harness.ServerTask;
            Assert.Fail("The pipe server loop ended while the service was still running.");
        }

        MihomoServiceIpcResponse response = await exchange;
        Assert.True(response.Succeeded);
        Assert.Equal(hello.RequestId, response.RequestId);
        Assert.Null(response.ValidateFor(hello));
        Assert.False(harness.ServerTask.IsCompleted);
    }

    private static Dictionary<string, Func<string, byte[]>> BuildCorpus()
    {
        Dictionary<string, Func<string, byte[]>> corpus = new(StringComparer.Ordinal)
        {
            ["empty-connection"] = _ => [],
            ["header-one-byte"] = _ => [0x10],
            ["header-three-bytes"] = _ => [0x10, 0x00, 0x00],
            ["length-without-payload"] = _ => PipeServerHarness.Frame(100, []),
            ["payload-truncated-by-one"] = token =>
            {
                byte[] full = PipeServerHarness.Frame(ValidRequestJson(token));
                return full[..^1];
            },
            ["length-zero"] = _ => PipeServerHarness.Frame(0, []),
            ["length-negative"] = _ => PipeServerHarness.Frame(-1, []),
            ["length-int-min"] = _ => PipeServerHarness.Frame(int.MinValue, []),
            ["length-int-max"] = _ => PipeServerHarness.Frame(int.MaxValue, []),
            ["length-one-over-limit"] = _ => PipeServerHarness.Frame(
                MihomoServiceIpcProtocol.MaximumFrameBytes + 1,
                []),
            ["not-json"] = _ => PipeServerHarness.Frame("this is not json"),
            ["json-array"] = _ => PipeServerHarness.Frame("[]"),
            ["json-null"] = _ => PipeServerHarness.Frame("null"),
            ["json-string"] = _ => PipeServerHarness.Frame("\"hello\""),
            ["json-number"] = _ => PipeServerHarness.Frame("12345"),
            ["empty-object-has-no-correlation-id"] = _ => PipeServerHarness.Frame("{}"),
            ["unknown-member"] = token => PipeServerHarness.Frame(
                ValidRequestJson(token).Replace("{", "{\"unexpected\":1,", StringComparison.Ordinal)),
            ["duplicate-member"] = token => PipeServerHarness.Frame(
                ValidRequestJson(token).Replace("{", "{\"command\":1,", StringComparison.Ordinal)),
            ["request-id-not-a-guid"] = token => PipeServerHarness.Frame(
                ValidRequestJson(token).Replace(
                    ReadRequestId(token),
                    "not-a-guid",
                    StringComparison.Ordinal)),
            ["version-wrong-type"] = token => PipeServerHarness.Frame(
                ValidRequestJson(token).Replace(
                    "\"protocolVersion\":" + MihomoServiceIpcProtocol.CurrentVersion,
                    "\"protocolVersion\":\"four\"",
                    StringComparison.Ordinal)),
            ["nesting-beyond-reader-depth"] = _ => PipeServerHarness.Frame(
                new string('[', 200) + new string(']', 200)),
            ["invalid-utf8"] = _ => PipeServerHarness.Frame(3, [0xFF, 0xFE, 0xFD]),
            ["utf8-bom-prefix"] = token =>
            {
                byte[] json = Encoding.UTF8.GetBytes(ValidRequestJson(token));
                byte[] payload = [0xEF, 0xBB, 0xBF, .. json];
                return PipeServerHarness.Frame(payload.Length, payload);
            },
            ["trailing-garbage"] = token => PipeServerHarness.Frame(ValidRequestJson(token) + "xyz"),
            ["whitespace-only"] = _ => PipeServerHarness.Frame("   "),
            ["nul-bytes"] = _ => PipeServerHarness.Frame(8, new byte[8]),
        };
        return corpus;
    }

    private static string ReadRequestId(string token)
    {
        using JsonDocument document = JsonDocument.Parse(ValidRequestJson(token));
        return document.RootElement.GetProperty("requestId").GetString()!;
    }

    private static string ValidRequestJson(string token)
    {
        // The request id is fixed so replacements are deterministic.
        MihomoServiceIpcRequest request = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = new Guid("11111111-2222-3333-4444-555555555555"),
            AuthenticationToken = token,
            Command = MihomoServiceIpcCommand.Hello,
        };
        return JsonSerializer.Serialize(request, WireOptions);
    }
}

using System.Text;
using System.Text.Json;
using ClashSharp.MihomoService;

namespace ClashSharp.Tests.Unit.MihomoService;

public sealed class MihomoConnectionResponseReaderTests
{
    [Theory]
    [InlineData(25000, 0)]
    [InlineData(2049, 49)]
    public async Task LargeDocuments_PreserveCountersBeyondByteAndNodeDetailBudgets(int count, int padding)
    {
        string[] chain = ["DIRECT"];
        string json = JsonSerializer.Serialize(new
        {
            connections = Enumerable.Range(0, count).Select(i => new
            {
                id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                metadata = new { host = "example.invalid", process = "test.exe" },
                ignored = Enumerable.Range(0, padding).ToArray(),
                rule = "MATCH",
                rulePayload = "",
                chains = chain,
                upload = 1,
                download = 2,
                start = "2026-11-05T00:00:00Z",
            }),
            // Counters may occur after the omitted array, including across stream boundaries.
            uploadTotal = 1234,
            downloadTotal = 5678,
        });
        await using ChunkedStream stream = new(Encoding.UTF8.GetBytes(json), 137);
        var result = await MihomoConnectionResponseReader.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(count, result.OmittedCount);
        Assert.True(result.Content.Length < 256);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal(1234, document.RootElement.GetProperty("uploadTotal").GetInt64());
        Assert.Equal(5678, document.RootElement.GetProperty("downloadTotal").GetInt64());
    }

    [Theory]
    [InlineData("{\"connections\":[],\"uploadTotal\":1,\"uploadTotal\":2,\"downloadTotal\":3}")]
    [InlineData("{\"connections\":[null],\"uploadTotal\":1,\"downloadTotal\":3}")]
    [InlineData("{\"connections\":[],\"uploadTotal\":-1,\"downloadTotal\":3}")]
    public async Task InvalidCountersOrRootStructure_AreRejected(string json)
    {
        await using ChunkedStream stream = new(Encoding.UTF8.GetBytes(json), 7);
        await Assert.ThrowsAsync<InvalidDataException>(() => MihomoConnectionResponseReader.ReadAsync(stream, CancellationToken.None));
    }

    private sealed class ChunkedStream(byte[] bytes, int chunk) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }
}

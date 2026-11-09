using System.Text;
using System.Text.Json;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Differential and boundary properties of the bounded streaming connection scanner: chunking must never change the
/// result, results must agree with a full DOM parse, retention and omission obey their budgets, and malformed
/// documents fail with typed errors only.
/// </summary>
public sealed class MihomoConnectionResponseReaderPropertyTests
{
    private const int DetailBudgetBytes = 512 * 1024;

    /// <summary>Documents covering ordering of counters, nulls, confusable keys, escapes, whitespace and extremes.</summary>
    /// <returns>Document names and texts.</returns>
    public static TheoryData<string, string> Corpus() => new()
    {
        { "empty", "{\"connections\":[],\"uploadTotal\":1,\"downloadTotal\":2}" },
        { "null-connections", "{\"connections\":null,\"uploadTotal\":0,\"downloadTotal\":0}" },
        { "counters-first", "{\"uploadTotal\":5,\"downloadTotal\":6,\"connections\":[{\"id\":\"a\"},{\"id\":\"b\"}]}" },
        { "extra-keys", "{\"memory\":123,\"extra\":{\"a\":[1,2,{\"b\":null}]},\"connections\":[{\"id\":\"a\",\"x\":[[],[{}]]}],\"uploadTotal\":7,\"downloadTotal\":8,\"tail\":[true,false,null]}" },
        { "confusable-keys-inside-rows", "{\"connections\":[{\"uploadTotal\":-5,\"connections\":[{\"id\":1}],\"downloadTotal\":\"x\"},{\"id\":\"b\"}],\"uploadTotal\":9,\"downloadTotal\":10}" },
        { "escapes-and-unicode", "{\"connections\":[{\"id\":\"}]{[\\\"\\\\\",\"u\":\"\\u00e9\\ud83d\\ude00\",\"raw\":\"😀中文\"}],\"uploadTotal\":11,\"downloadTotal\":12}" },
        { "pretty-crlf-tabs", "{\r\n\t\"connections\" :\t[\r\n\t\t{ \"id\" : \"a\" } ,\r\n\t\t{ \"id\" : \"b\" }\r\n\t] ,\r\n\t\"uploadTotal\" : 13 ,\r\n\t\"downloadTotal\" : 14\r\n}\r\n" },
        { "max-counters", "{\"connections\":[],\"uploadTotal\":9223372036854775807,\"downloadTotal\":9223372036854775807}" },
        { "nested-rows", "{\"connections\":[{\"metadata\":{\"a\":{\"b\":{\"c\":[1,2,3]}}}},{}],\"uploadTotal\":1,\"downloadTotal\":1}" },
    };

    /// <summary>Verifies a corpus document yields identical output for every single split position and many chunk sizes.</summary>
    /// <param name="name">Document name.</param>
    /// <param name="json">Document text.</param>
    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Output_IsIndependentOfChunkBoundaries_AndAgreesWithADomParse(string name, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        (byte[] Content, int? OmittedCount) reference = await ReadAsync(bytes, [bytes.Length]);
        AssertAgreesWithDom(name, bytes, reference);

        for (int split = 1; split < bytes.Length; split++)
        {
            Assert.Equal(reference, await ReadAsync(bytes, [split, bytes.Length]), ResultComparer.Instance);
        }

        foreach (int size in new[] { 1, 2, 3, 5, 7, 16, 64, 1000 })
        {
            Assert.Equal(reference, await ReadAsync(bytes, [size], repeat: true), ResultComparer.Instance);
        }

        Assert.Null(reference.OmittedCount);
        Assert.Equal(bytes, reference.Content);
    }

    /// <summary>Verifies the row-count and byte budgets switch to omission at exactly their boundaries.</summary>
    [Fact]
    public async Task RetentionBudgets_SwitchAtExactBoundaries()
    {
        foreach (int rows in new[] { 2047, 2048, 2049, 5000 })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Document(rows, paddingBytes: 0));
            (byte[] content, int? omitted) = await ReadAsync(bytes, [4096], repeat: true);
            Assert.True(rows > 2048 == (omitted is not null), $"rows={rows} omitted={omitted}");
            AssertAgreesWithDom($"rows={rows}", bytes, (content, omitted));
        }

        string baseline = Document(1, 0);
        int slack = DetailBudgetBytes - Encoding.UTF8.GetByteCount(baseline);
        foreach (int delta in new[] { -1, 0, 1 })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Document(1, slack + delta));
            Assert.Equal(DetailBudgetBytes + delta, bytes.Length);
            (byte[] content, int? omitted) = await ReadAsync(bytes, [8192], repeat: true);
            Assert.True(delta > 0 == (omitted is not null), $"delta={delta} omitted={omitted}");
            if (omitted is null)
            {
                Assert.Equal(bytes, content);
            }
            else
            {
                Assert.Equal(1, omitted);
                Assert.True(content.Length < 256);
            }
        }

        // The scanner keeps row details only while the document has at most 80,000 JSON tokens.
        int baseTokens = CountTokens(NodeDocument(0));
        foreach (int tokens in new[] { 79_999, 80_000, 80_001, 100_000 })
        {
            byte[] bytes = NodeDocument(tokens - baseTokens);
            Assert.Equal(tokens, CountTokens(bytes));
            (byte[] content, int? omitted) = await ReadAsync(bytes, [30_011], repeat: true);
            Assert.True(tokens > 80_000 == (omitted is not null), $"tokens={tokens} omitted={omitted}");
            Assert.Equal(await ReadAsync(bytes, [bytes.Length]), (content, omitted), ResultComparer.Instance);
        }
    }

    /// <summary>Verifies a single oversized token (string, number, property name) is rejected rather than buffered.</summary>
    [Fact]
    public async Task OversizedTokens_AreRejected()
    {
        string[] documents =
        [
            "{\"connections\":[{\"id\":\"" + new string('a', 70_000) + "\"}],\"uploadTotal\":1,\"downloadTotal\":2}",
            "{\"connections\":[],\"uploadTotal\":" + new string('1', 70_000) + ",\"downloadTotal\":2}",
            "{\"" + new string('k', 70_000) + "\":1,\"connections\":[],\"uploadTotal\":1,\"downloadTotal\":2}",
        ];
        foreach (string document in documents)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(document);
            await AssertTypedFailureAsync(bytes);
        }

        byte[] large = Encoding.UTF8.GetBytes("{\"connections\":[{\"id\":\"" + new string('a', 60_000) + "\"}],\"uploadTotal\":1,\"downloadTotal\":2}");
        (byte[] content, int? omitted) = await ReadAsync(large, [4096], repeat: true);
        Assert.Null(omitted);
        Assert.Equal(large, content);
    }

    /// <summary>Verifies truncation at every offset is a typed failure (never a silent partial snapshot).</summary>
    [Fact]
    public async Task Truncation_AtEveryOffset_IsATypedFailure()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            "{\"connections\":[{\"id\":\"a\",\"m\":{\"k\":[1,2]}},{\"id\":\"b\"}],\"uploadTotal\":15,\"downloadTotal\":16}");
        for (int length = 0; length < bytes.Length; length++)
        {
            await AssertTypedFailureAsync(bytes[..length]);
        }

        (byte[] _, int? omitted) = await ReadAsync(bytes, [bytes.Length]);
        Assert.Null(omitted);
    }

    /// <summary>Verifies documents lacking either counter, with a wrong shape or with trailing structure are rejected.</summary>
    /// <param name="json">Hostile document.</param>
    [Theory]
    [InlineData("{\"connections\":[]}")]
    [InlineData("{\"uploadTotal\":1,\"downloadTotal\":2}")]
    [InlineData("{\"connections\":[],\"uploadTotal\":1}")]
    [InlineData("{\"connections\":[],\"downloadTotal\":1}")]
    [InlineData("{\"connections\":{},\"uploadTotal\":1,\"downloadTotal\":2}")]
    [InlineData("{\"connections\":\"x\",\"uploadTotal\":1,\"downloadTotal\":2}")]
    [InlineData("{\"connections\":[1],\"uploadTotal\":1,\"downloadTotal\":2}")]
    [InlineData("{\"connections\":[[]],\"uploadTotal\":1,\"downloadTotal\":2}")]
    [InlineData("{\"connections\":[],\"connections\":[],\"uploadTotal\":1,\"downloadTotal\":2}")]
    [InlineData("{\"connections\":[],\"uploadTotal\":1.5,\"downloadTotal\":2}")]
    [InlineData("{\"connections\":[],\"uploadTotal\":\"1\",\"downloadTotal\":2}")]
    [InlineData("{\"connections\":[],\"uploadTotal\":9223372036854775808,\"downloadTotal\":2}")]
    [InlineData("{\"connections\":[],\"uploadTotal\":1e2,\"downloadTotal\":2}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("1")]
    [InlineData("{\"connections\":[],\"uploadTotal\":1,\"downloadTotal\":2}{}")]
    [InlineData("{\"connections\":[],\"uploadTotal\":1,\"downloadTotal\":2},")]
    [InlineData("")]
    public async Task MalformedDocuments_AreRejected(string json)
    {
        await AssertTypedFailureAsync(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Verifies invalid UTF-8 inside a root property name is a typed failure, as the callers only handle typed failures.</summary>
    [Fact]
    public async Task InvalidUtf8InARootPropertyName_IsATypedFailure()
    {
        foreach (byte invalid in new byte[] { 0xFF, 0xC0, 0x80, 0xE3 })
        {
            byte[] bytes = [.. Encoding.UTF8.GetBytes("{\"conn"), invalid, .. Encoding.UTF8.GetBytes("ections\":[],\"uploadTotal\":1,\"downloadTotal\":2}")];
            await AssertTypedFailureAsync(bytes);
        }
    }

    /// <summary>Verifies nesting beyond the depth limit is rejected, at the root and inside rows.</summary>
    [Fact]
    public async Task ExcessiveNesting_IsRejected()
    {
        string deepRoot = "{\"connections\":[],\"uploadTotal\":1,\"downloadTotal\":2,\"x\":" + new string('[', 40) + new string(']', 40) + "}";
        string deepRow = "{\"connections\":[{\"x\":" + new string('[', 40) + new string(']', 40) + "}],\"uploadTotal\":1,\"downloadTotal\":2}";
        string shallow = "{\"connections\":[{\"x\":" + new string('[', 20) + new string(']', 20) + "}],\"uploadTotal\":1,\"downloadTotal\":2}";

        await AssertTypedFailureAsync(Encoding.UTF8.GetBytes(deepRoot));
        await AssertTypedFailureAsync(Encoding.UTF8.GetBytes(deepRow));
        (byte[] _, int? omitted) = await ReadAsync(Encoding.UTF8.GetBytes(shallow), [100], repeat: true);
        Assert.Null(omitted);
    }

    /// <summary>Verifies random byte-level mutations never escape as untyped failures and success always agrees with a DOM parse.</summary>
    [Fact]
    public async Task RandomMutations_AreTypedFailuresOrDomConsistentSuccesses()
    {
        Random random = new(SettingsGenerators.Seed);
        byte[] original = Encoding.UTF8.GetBytes(Document(6, 40).Replace("\"id\"", "\"i\\u0064\"", StringComparison.Ordinal));
        int successes = 0;
        for (int round = 0; round < 1500; round++)
        {
            byte[] mutated = [.. original];
            int edits = 1 + random.Next(3);
            for (int edit = 0; edit < edits; edit++)
            {
                int position = random.Next(mutated.Length);
                switch (random.Next(4))
                {
                    case 0: mutated[position] ^= (byte)(1 << random.Next(8)); break;
                    case 1: mutated = [.. mutated[..position], .. mutated[(position + 1)..]]; break;
                    case 2: mutated = [.. mutated[..position], (byte)random.Next(256), .. mutated[position..]]; break;
                    default: mutated[position] = (byte)"{}[]\",:0 -e."[random.Next(12)]; break;
                }

                if (mutated.Length == 0)
                {
                    mutated = [0];
                }
            }

            (byte[] Content, int? OmittedCount)? result = null;
            try
            {
                result = await ReadAsync(mutated, [1 + random.Next(40)], repeat: true);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
            }

            if (result is not null)
            {
                successes++;
                AssertAgreesWithDom($"mutation {round}", mutated, result.Value);
            }
        }

        Assert.True(successes > 0 && successes < 1500, "the mutation set must contain both outcomes");
    }

    /// <summary>Verifies a document beyond the absolute scan budget is cut off near the limit without retaining rows.</summary>
    [Fact]
    public async Task DocumentsBeyondTheScanBudget_AreCutOffNearTheLimit()
    {
        using GeneratedStream stream = new(70L * 1024 * 1024);

        await Assert.ThrowsAsync<InvalidDataException>(() => MihomoConnectionResponseReader.ReadAsync(stream, CancellationToken.None));

        Assert.InRange(stream.Produced, 64L * 1024 * 1024, (64L * 1024 * 1024) + (128 * 1024));
    }

    /// <summary>Verifies cancellation of a pending read propagates and an already cancelled token never reads.</summary>
    [Fact]
    public async Task Cancellation_PropagatesWithoutConsumingTheStream()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        byte[] bytes = Encoding.UTF8.GetBytes(Document(1, 0));
        await using ChunkedStream stream = new(bytes, [bytes.Length], repeat: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MihomoConnectionResponseReader.ReadAsync(stream, cancellation.Token));

        Assert.Equal(0, stream.Position);
    }

    private static byte[] NodeDocument(int numbers) => Encoding.UTF8.GetBytes(
        "{\"connections\":[{\"x\":[" + string.Join(",", Enumerable.Repeat("1", numbers)) + "]}],\"uploadTotal\":1,\"downloadTotal\":2}");

    private static int CountTokens(byte[] json)
    {
        Utf8JsonReader reader = new(json);
        int count = 0;
        while (reader.Read())
        {
            count++;
        }

        return count;
    }

    /// <summary>Builds a document with <paramref name="rows"/> rows and exactly <paramref name="paddingBytes"/> extra bytes spread over small members.</summary>
    private static string Document(int rows, int paddingBytes)
    {
        StringBuilder builder = new("{\"connections\":[");
        for (int row = 0; row < rows; row++)
        {
            builder.Append(row == 0 ? string.Empty : ",");
            builder.Append("{\"id\":\"").Append(row).Append('"');
            if (row == 0)
            {
                int remaining = paddingBytes;
                int member = 0;
                while (remaining > 0)
                {
                    string name = ",\"p" + member.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "\":\"";
                    int overhead = name.Length + 1;
                    int value = remaining > 1000 + overhead + overhead ? 1000 : remaining - overhead;
                    Assert.True(value >= 0, "padding must be representable");
                    builder.Append(name).Append('p', value).Append('"');
                    remaining -= overhead + value;
                    member++;
                }
            }

            builder.Append('}');
        }

        builder.Append("],\"uploadTotal\":123,\"downloadTotal\":456}");
        return builder.ToString();
    }

    private static async Task<(byte[] Content, int? OmittedCount)> ReadAsync(byte[] bytes, int[] chunkPlan, bool repeat = false)
    {
        await using ChunkedStream stream = new(bytes, chunkPlan, repeat);
        return await MihomoConnectionResponseReader.ReadAsync(stream, CancellationToken.None);
    }

    private static async Task AssertTypedFailureAsync(byte[] bytes)
    {
        foreach (int[] plan in new int[][] { [Math.Max(bytes.Length, 1)], [1], [7] })
        {
            try
            {
                await ReadAsync(bytes, plan, repeat: true);
                Assert.Fail("A malformed connection document was accepted.");
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
            }
        }
    }

    private static void AssertAgreesWithDom(string name, byte[] source, (byte[] Content, int? OmittedCount) result)
    {
        using JsonDocument original = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = original.RootElement;
        JsonElement connections = root.GetProperty("connections");
        int rows = connections.ValueKind == JsonValueKind.Array ? connections.GetArrayLength() : 0;
        using JsonDocument projected = JsonDocument.Parse(result.Content);
        Assert.Equal(root.GetProperty("uploadTotal").GetInt64(), projected.RootElement.GetProperty("uploadTotal").GetInt64());
        Assert.Equal(root.GetProperty("downloadTotal").GetInt64(), projected.RootElement.GetProperty("downloadTotal").GetInt64());
        if (result.OmittedCount is int omitted)
        {
            Assert.True(rows == omitted, $"{name}: omitted count {omitted} differs from {rows} rows");
            Assert.Equal(JsonValueKind.Null, projected.RootElement.GetProperty("connections").ValueKind);
        }
        else
        {
            Assert.Equal(source, result.Content);
            Assert.True(rows <= 2048, $"{name}: a document with {rows} rows must not be retained");
        }
    }

    private sealed class ResultComparer : IEqualityComparer<(byte[] Content, int? OmittedCount)>
    {
        public static ResultComparer Instance { get; } = new();

        public bool Equals((byte[] Content, int? OmittedCount) x, (byte[] Content, int? OmittedCount) y) =>
            x.OmittedCount == y.OmittedCount && x.Content.AsSpan().SequenceEqual(y.Content);

        public int GetHashCode((byte[] Content, int? OmittedCount) obj) => HashCode.Combine(obj.OmittedCount, obj.Content.Length);
    }

    private sealed class ChunkedStream(byte[] data, int[] plan, bool repeat) : Stream
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
            int size = _reads < plan.Length ? plan[_reads] : (repeat ? plan[^1] : buffer.Length);
            _reads++;
            int take = Math.Min(Math.Min(buffer.Length, size), data.Length - _position);
            data.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GeneratedStream(long total) : Stream
    {
        private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("{\"connections\":[");
        private static readonly byte[] Row = Encoding.ASCII.GetBytes("{\"id\":\"c\"},");
        private int _cursor;
        private bool _inRows;

        public long Produced { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => total;

        public override long Position
        {
            get => Produced;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int written = 0;
            while (written < buffer.Length && Produced < total)
            {
                byte[] source = _inRows ? Row : Prefix;
                buffer[written++] = source[_cursor++];
                Produced++;
                if (_cursor == source.Length)
                {
                    _cursor = 0;
                    _inRows = true;
                }
            }

            return written;
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

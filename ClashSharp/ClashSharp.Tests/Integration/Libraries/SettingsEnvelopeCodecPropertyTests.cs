using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Libraries;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration.Libraries;

/// <summary>
/// Properties of the settings envelope codec: canonical round trip over generated envelopes, rejection of
/// corruption at every byte offset and of every structural mutation, and the schema upgrade chain.
/// </summary>
public sealed class SettingsEnvelopeCodecPropertyTests
{
    private static readonly SettingsRegistry Default = SettingsRegistry.Default;

    /// <summary>Verifies decode(encode(x)) preserves every field and that encoding is canonical for generated envelopes.</summary>
    /// <param name="seedOffset">Offset added to the fixed seed.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RoundTrip_GeneratedEnvelopes_PreserveEveryFieldAndAreCanonical(int seedOffset)
    {
        Random random = new(SettingsGenerators.Seed + seedOffset);
        for (int round = 0; round < 6; round++)
        {
            SettingsEnvelope source = SettingsGenerators.RandomEnvelope(Default, random, 5 + random.Next(60));
            SettingsEnvelope withHistory = WithMigrationHistory(source, random);

            SettingsEnvelopeCodec.EncodedSettingsEnvelope encoded = SettingsEnvelopeCodec.Encode(withHistory, Default);
            SettingsEnvelope decoded = SettingsEnvelopeCodec.Decode(encoded.Bytes, Default);

            AssertEquivalent(withHistory, decoded);
            SettingsEnvelopeCodec.EncodedSettingsEnvelope again = SettingsEnvelopeCodec.Encode(decoded, Default);
            Assert.Equal(encoded.Bytes, again.Bytes);
            Assert.Equal(encoded.ContentHash, again.ContentHash);
            Assert.Equal(encoded.ContentHash, Convert.ToHexStringLower(SHA256.HashData(PayloadOf(encoded.Bytes))));
        }
    }

    /// <summary>Verifies the encoder refuses every envelope the validator rejects instead of persisting it.</summary>
    [Fact]
    public void Encode_RefusesEnvelopesThatViolateTheSchema()
    {
        SettingsEnvelope valid = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Dictionary<SettingKey, SettingDesiredEntry> desired = valid.Desired.ToDictionary();
        SettingKey first = desired.Keys.First();
        desired.Remove(first);
        SettingsEnvelope missing = new(valid.SchemaVersion, valid.EnvelopeRevision, desired, valid.Applied, [], []);
        SettingsEnvelope ahead = new(
            valid.SchemaVersion,
            1,
            valid.Desired.ToDictionary(pair => pair.Key, pair => new SettingDesiredEntry(pair.Value.Value, 5)),
            valid.Applied,
            [],
            []);
        SettingsEnvelope futureSchema = new(
            SettingsEnvelope.CurrentSchemaVersion + 1, valid.EnvelopeRevision, valid.Desired, valid.Applied, [], []);

        foreach (SettingsEnvelope invalid in new[] { missing, ahead, futureSchema })
        {
            SettingsEnvelopeCodecException exception = Assert.Throws<SettingsEnvelopeCodecException>(
                () => SettingsEnvelopeCodec.Encode(invalid, Default));
            Assert.Equal("settings.persistence.envelope.domain_invalid", exception.Code);
        }
    }

    /// <summary>
    /// Verifies that any single-byte corruption, truncation, deletion or insertion at every offset is rejected
    /// with the codec's typed failure and can never yield a different accepted envelope.
    /// </summary>
    [Fact]
    public void Decode_CorruptionAtEveryByteOffset_IsAlwaysTypedAndNeverAcceptsDifferentContent()
    {
        SettingsRegistry registry = SettingsEnvelopeTestData.CreateLiveAndRestartRegistry();
        SettingsEnvelope envelope = SettingsGenerators.RandomEnvelope(registry, new Random(SettingsGenerators.Seed), 6);
        byte[] original = SettingsEnvelopeCodec.Encode(envelope, registry).Bytes;
        int accepted = 0;

        void Probe(byte[] candidate, string description)
        {
            try
            {
                SettingsEnvelope decoded = SettingsEnvelopeCodec.Decode(candidate, registry);
                accepted++;
                Assert.True(
                    candidate.AsSpan().SequenceEqual(original),
                    $"{description}: corrupted bytes were accepted as a different envelope");
                AssertEquivalent(envelope, decoded);
            }
            catch (SettingsEnvelopeCodecException exception)
            {
                Assert.StartsWith("settings.persistence.envelope.", exception.Code, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(exception.Path));
            }
        }

        Probe(original, "identity");
        Assert.Equal(1, accepted);
        for (int offset = 0; offset < original.Length; offset++)
        {
            foreach (int bit in new[] { 0, 3, 7 })
            {
                byte[] flipped = (byte[])original.Clone();
                flipped[offset] ^= (byte)(1 << bit);
                Probe(flipped, $"bit {bit} at {offset}");
            }

            Probe(original[..offset], $"truncated to {offset}");
            Probe([.. original[..offset], .. original[(offset + 1)..]], $"deleted byte {offset}");
            foreach (byte inserted in new byte[] { (byte)' ', (byte)'0', (byte)'"', 0xFF })
            {
                Probe([.. original[..offset], inserted, .. original[offset..]], $"inserted {inserted} at {offset}");
            }
        }

        Probe([.. original, (byte)'\n'], "trailing newline");
        Probe([.. Encoding.UTF8.GetPreamble(), .. original], "byte order mark");
        Assert.Equal(1, accepted);
    }

    /// <summary>Verifies sampled byte offsets of a full default-registry envelope behave like the exhaustive small case.</summary>
    [Fact]
    public void Decode_CorruptionOfAFullEnvelope_IsRejectedAtSampledOffsets()
    {
        Random random = new(SettingsGenerators.Seed);
        SettingsEnvelope envelope = SettingsGenerators.RandomEnvelope(Default, random, 40);
        byte[] original = SettingsEnvelopeCodec.Encode(envelope, Default).Bytes;

        for (int sample = 0; sample < 400; sample++)
        {
            int offset = sample < 40 ? sample : random.Next(original.Length);
            byte[] mutated = (byte[])original.Clone();
            mutated[offset] ^= (byte)(1 << random.Next(8));
            Assert.Throws<SettingsEnvelopeCodecException>(() => SettingsEnvelopeCodec.Decode(mutated, Default));
            Assert.Throws<SettingsEnvelopeCodecException>(() => SettingsEnvelopeCodec.Decode(original.AsMemory(0, offset), Default));
        }
    }

    /// <summary>Verifies arbitrary garbage and degenerate JSON shapes only ever produce the codec's typed failure.</summary>
    [Fact]
    public void Decode_GarbageAndDegenerateJson_OnlyProduceTypedFailures()
    {
        Random random = new(SettingsGenerators.Seed);
        List<byte[]> inputs = [[], [0], [0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("null"), Encoding.UTF8.GetBytes("[]"),
            Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("\"x\""), Encoding.UTF8.GetBytes("1e999"),
            Encoding.UTF8.GetBytes("{\"formatVersion\":1}"),
            Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"payload\":null,\"contentHash\":null}"),
            Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"payload\":\"%%%%\",\"contentHash\":\"00\"}"),
            Encoding.UTF8.GetBytes("{\"formatVersion\":1.0,\"payload\":\"\",\"contentHash\":\"" + new string('0', 64) + "\"}"),
            Encoding.UTF8.GetBytes("{\"formatVersion\":99999999999999999999,\"payload\":\"\",\"contentHash\":\"x\"}"),
            Encoding.UTF8.GetBytes(new string('[', 5000)), Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"a\":", 200)))];
        for (int i = 0; i < 300; i++)
        {
            byte[] noise = new byte[random.Next(1, 200)];
            random.NextBytes(noise);
            inputs.Add(noise);
        }

        foreach (byte[] input in inputs)
        {
            Assert.Throws<SettingsEnvelopeCodecException>(() => SettingsEnvelopeCodec.Decode(input, Default));
        }

        foreach (byte[] payload in new byte[][]
        {
            [], Encoding.UTF8.GetBytes("null"), Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("{}"),
            Encoding.UTF8.GetBytes("{\"schemaVersion\":4}"), [0xFF, 0xFE, 0xFD],
        })
        {
            byte[] wrapped = Wrap(payload);
            SettingsEnvelopeCodecException exception = Assert.Throws<SettingsEnvelopeCodecException>(
                () => SettingsEnvelopeCodec.Decode(wrapped, Default));
            Assert.StartsWith("settings.persistence.envelope.", exception.Code, StringComparison.Ordinal);
        }

    }

    /// <summary>
    /// Verifies every structural mutation of a correctly hashed payload (delete, null, retype, extreme numbers) is
    /// rejected with a typed failure, or round-trips canonically; no mutation escapes as an untyped exception.
    /// </summary>
    [Fact]
    public void Decode_StructuralMutationOfEveryPayloadNode_NeverEscapesAsUntypedFailure()
    {
        SettingsRegistry registry = SettingsEnvelopeTestData.CreateLiveAndRestartRegistry();
        SettingsEnvelope envelope = SettingsGenerators.RandomEnvelope(registry, new Random(SettingsGenerators.Seed + 9), 5);
        envelope = WithMigrationHistory(envelope, new Random(SettingsGenerators.Seed));
        byte[] encoded = SettingsEnvelopeCodec.Encode(envelope, registry).Bytes;
        JsonNode template = JsonNode.Parse(PayloadOf(encoded))!;
        List<string[]> paths = [];
        Collect(template, [], paths);
        int rejected = 0;
        int mutants = 0;

        foreach (string[] path in paths)
        {
            foreach ((string name, Action<JsonNode> mutate) in Mutations(path))
            {
                JsonNode copy = JsonNode.Parse(PayloadOf(encoded))!;
                mutate(copy);
                byte[] wrapped = Wrap(Encoding.UTF8.GetBytes(copy.ToJsonString()));
                mutants++;
                try
                {
                    SettingsEnvelope decoded = SettingsEnvelopeCodec.Decode(wrapped, registry);
                    Assert.Equal(wrapped, SettingsEnvelopeCodec.Encode(decoded, registry).Bytes);
                }
                catch (SettingsEnvelopeCodecException exception)
                {
                    rejected++;
                    Assert.StartsWith("settings.persistence.envelope.", exception.Code, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(mutants > 300, "the mutation universe must be substantial");
        Assert.True(rejected > mutants / 2, "most structural mutations must be rejected");

        foreach (string field in new[] { "schemaVersion", "envelopeRevision", "desired", "applied", "pendingApplications", "migrationHistory" })
        {
            JsonNode copy = JsonNode.Parse(PayloadOf(encoded))!;
            copy.AsObject().Remove(field);
            Assert.Throws<SettingsEnvelopeCodecException>(
                () => SettingsEnvelopeCodec.Decode(Wrap(Encoding.UTF8.GetBytes(copy.ToJsonString())), registry));
        }
    }

    /// <summary>Verifies each historical schema upgrades by adding only that version's keys and recording a contiguous chain.</summary>
    /// <param name="version">Historical schema version being decoded.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Decode_UpgradesEveryHistoricalSchemaIntoTheCurrentOne(int version)
    {
        Assert.True(version <= SettingsEnvelope.CurrentSchemaVersion);
        Assert.Equal(4, SettingsEnvelope.CurrentSchemaVersion);
        SettingKey[] introducedAfter = [.. KeysIntroducedAfter(version)];
        SettingsRegistry historical = SettingsRegistry.Create(
            Default.Definitions.Where(definition => !introducedAfter.Contains(definition.Key)));
        SettingsEnvelope before = SettingsGenerators.RandomEnvelope(historical, new Random(SettingsGenerators.Seed + version), 30);
        byte[] bytes = Wrap(Encoding.UTF8.GetBytes(WithSchemaVersion(PayloadOf(SettingsEnvelopeCodec.Encode(before, historical).Bytes), version)));

        SettingsEnvelope after = SettingsEnvelopeCodec.Decode(bytes, Default);

        Assert.Equal(SettingsEnvelope.CurrentSchemaVersion, after.SchemaVersion);
        Assert.Equal(before.EnvelopeRevision, after.EnvelopeRevision);
        foreach ((SettingKey key, SettingDesiredEntry entry) in before.Desired)
        {
            Assert.Equal(entry, after.Desired[key]);
            Assert.Equal(before.Applied[key], after.Applied[key]);
        }

        Assert.Equal(before.Desired.Count + introducedAfter.Length, after.Desired.Count);
        foreach (SettingKey key in introducedAfter)
        {
            Assert.Equal(Default.Get(key.Value).DefaultValue, after.Desired[key].Value);
            Assert.Equal(1, after.Desired[key].KeyDesiredRevision);
            Assert.Equal(SettingAppliedStateKind.Unknown, after.Applied[key].Kind);
            Assert.True(after.Applied[key].RequiresApplication);
            Assert.Single(after.PendingApplications, batch => batch.Entries.Any(entry => entry.Key == key));
        }

        Assert.Equal(
            Enumerable.Range(version, SettingsEnvelope.CurrentSchemaVersion - version),
            after.MigrationHistory.Skip(before.MigrationHistory.Count).Select(static record => record.FromSchemaVersion));
        Assert.True(new SettingsEnvelopeValidator(Default).Validate(after).IsValid);
        SettingsEnvelope reread = SettingsEnvelopeCodec.Decode(SettingsEnvelopeCodec.Encode(after, Default).Bytes, Default);
        AssertEquivalent(after, reread);
        Assert.Equal(
            SettingsEnvelopeCodec.Decode(bytes, Default).MigrationHistory.Select(static record => record.MigrationId),
            after.MigrationHistory.Select(static record => record.MigrationId));
    }

    /// <summary>Verifies unsupported schema versions are refused whatever the envelope says about itself.</summary>
    /// <param name="version">Schema version stamped into an otherwise valid payload.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void Decode_RefusesSchemaVersionsOutsideTheKnownChain(int version)
    {
        SettingsEnvelope envelope = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        string payload = WithSchemaVersion(PayloadOf(SettingsEnvelopeCodec.Encode(envelope, Default).Bytes), version);

        Assert.Throws<SettingsEnvelopeCodecException>(
            () => SettingsEnvelopeCodec.Decode(Wrap(Encoding.UTF8.GetBytes(payload)), Default));
    }

    private static IEnumerable<SettingKey> KeysIntroducedAfter(int version)
    {
        // Schema history: version 2 added display units, 3 added the local agent, 4 added tile templates.
        Dictionary<int, SettingKey[]> introducedAt = new()
        {
            [2] = [SettingsRegistry.Keys.DataUnitStyle, SettingsRegistry.Keys.RateUnit, SettingsRegistry.Keys.ClockFormat],
            [3] = [SettingsRegistry.Keys.McpServerEnabled, SettingsRegistry.Keys.McpServerPort, SettingsRegistry.Keys.McpAllowedScopes],
            [4] = [SettingsRegistry.Keys.MasterInfoTilePresets],
        };
        return introducedAt.Where(pair => pair.Key > version).SelectMany(static pair => pair.Value);
    }

    private static string WithSchemaVersion(byte[] payload, int version)
    {
        JsonNode node = JsonNode.Parse(payload)!;
        node["schemaVersion"] = version;
        return node.ToJsonString();
    }

    private static SettingsEnvelope WithMigrationHistory(SettingsEnvelope source, Random random)
    {
        int count = random.Next(0, 4);
        List<SettingsMigrationRecord> history = [];
        int from = 0;
        for (int index = 0; index < count && from < SettingsEnvelope.CurrentSchemaVersion; index++)
        {
            int to = Math.Min(SettingsEnvelope.CurrentSchemaVersion, from + 1 + random.Next(2));
            history.Add(new SettingsMigrationRecord(
                SettingsGenerators.NextGuid(random),
                from,
                to,
                Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(random.Next())))));
            from = to;
        }

        return new SettingsEnvelope(
            source.SchemaVersion, source.EnvelopeRevision, source.Desired, source.Applied, source.PendingApplications, history);
    }

    private static byte[] PayloadOf(byte[] wrapper)
    {
        using JsonDocument document = JsonDocument.Parse(wrapper);
        return document.RootElement.GetProperty("payload").GetBytesFromBase64();
    }

    private static byte[] Wrap(byte[] payload)
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteBase64String("payload", payload);
            writer.WriteString("contentHash", Convert.ToHexStringLower(SHA256.HashData(payload)));
            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    private static void Collect(JsonNode? node, List<string> path, List<string[]> paths)
    {
        paths.Add([.. path]);
        switch (node)
        {
            case JsonObject obj:
                foreach ((string name, JsonNode? child) in obj)
                {
                    Collect(child, [.. path, name], paths);
                }

                break;
            case JsonArray array:
                for (int index = 0; index < array.Count; index++)
                {
                    Collect(array[index], [.. path, index.ToString(System.Globalization.CultureInfo.InvariantCulture)], paths);
                }

                break;
        }
    }

    private static JsonNode Navigate(JsonNode root, string[] path, int depth)
    {
        JsonNode current = root;
        for (int index = 0; index < depth; index++)
        {
            current = current is JsonArray array
                ? array[int.Parse(path[index], System.Globalization.CultureInfo.InvariantCulture)]!
                : current[path[index]]!;
        }

        return current;
    }

    private static IEnumerable<(string Name, Action<JsonNode> Mutate)> Mutations(string[] path)
    {
        if (path.Length == 0)
        {
            yield break;
        }

        void Replace(JsonNode root, JsonNode? value)
        {
            JsonNode parent = Navigate(root, path, path.Length - 1);
            if (parent is JsonArray array)
            {
                array[int.Parse(path[^1], System.Globalization.CultureInfo.InvariantCulture)] = value;
            }
            else
            {
                parent[path[^1]] = value;
            }
        }

        yield return ("delete", root =>
        {
            JsonNode parent = Navigate(root, path, path.Length - 1);
            if (parent is JsonArray array)
            {
                array.RemoveAt(int.Parse(path[^1], System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                parent.AsObject().Remove(path[^1]);
            }
        }
        );
        yield return ("null", root => Replace(root, null));
        yield return ("string", root => Replace(root, JsonValue.Create("x")));
        yield return ("number", root => Replace(root, JsonValue.Create(7)));
        yield return ("negative", root => Replace(root, JsonValue.Create(-1)));
        yield return ("max", root => Replace(root, JsonValue.Create(long.MaxValue)));
        yield return ("fraction", root => Replace(root, JsonValue.Create(1.5)));
        yield return ("bool", root => Replace(root, JsonValue.Create(true)));
        yield return ("array", root => Replace(root, new JsonArray()));
        yield return ("object", root => Replace(root, new JsonObject()));
        yield return ("emptyString", root => Replace(root, JsonValue.Create(string.Empty)));
        yield return ("guidish", root => Replace(root, JsonValue.Create(Guid.Empty.ToString("D"))));
    }

    private static void AssertEquivalent(SettingsEnvelope expected, SettingsEnvelope actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.EnvelopeRevision, actual.EnvelopeRevision);
        Assert.Equal(expected.Desired.Count, actual.Desired.Count);
        foreach ((SettingKey key, SettingDesiredEntry entry) in expected.Desired)
        {
            Assert.Equal(entry, actual.Desired[key]);
        }

        Assert.Equal(expected.Applied.Count, actual.Applied.Count);
        foreach ((SettingKey key, SettingAppliedState state) in expected.Applied)
        {
            Assert.Equal(state, actual.Applied[key]);
        }

        Assert.Equal(expected.PendingApplications.Count, actual.PendingApplications.Count);
        for (int index = 0; index < expected.PendingApplications.Count; index++)
        {
            SettingsApplicationBatch left = expected.PendingApplications[index];
            SettingsApplicationBatch right = actual.PendingApplications[index];
            Assert.Equal(left.BatchId, right.BatchId);
            Assert.Equal(left.AttemptId, right.AttemptId);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.State, right.State);
            Assert.Equal(left.ApplicationKind, right.ApplicationKind);
            Assert.Equal(left.CreationSequence, right.CreationSequence);
            Assert.Equal(left.LastError?.Code, right.LastError?.Code);
            Assert.Equal(left.Entries, right.Entries);
        }

        Assert.Equal(expected.MigrationHistory, actual.MigrationHistory);
    }
}

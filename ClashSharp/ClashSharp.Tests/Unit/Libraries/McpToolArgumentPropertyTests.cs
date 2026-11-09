using System.Text.Json;
using System.Text.Json.Nodes;
using ClashSharp.ApplicationModel.Agent;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Schema-driven properties of the MCP argument validator over the whole tool catalog: arguments generated from each
/// schema are accepted, and every single-rule violation (missing, extra, retyped, out-of-bounds, malformed) is
/// rejected. Schemas are read from the catalog, so new tools and new bounds are covered automatically.
/// </summary>
public sealed class McpToolArgumentPropertyTests
{
    private static readonly Dictionary<string, string> PatternSamples = new(StringComparer.Ordinal)
    {
        ["^[0-9a-f]{32}:[1-9][0-9]{0,18}$"] = "0123456789abcdef0123456789abcdef:42",
        ["^[a-fA-F0-9]{64}$"] = "0123456789abcdefABCDEF0123456789abcdefABCDEF0123456789abcdefABCD",
    };

    /// <summary>Enumerates every catalog tool.</summary>
    /// <returns>Theory rows.</returns>
    public static TheoryData<McpToolId> Tools() => [.. McpToolCatalog.All.Select(static tool => tool.Id)];

    /// <summary>Verifies arguments generated from the tool schema (minimal, maximal and random) are accepted.</summary>
    /// <param name="id">Tool under test.</param>
    [Theory]
    [MemberData(nameof(Tools))]
    public void GeneratedArguments_AreAccepted(McpToolId id)
    {
        McpToolDefinition tool = Definition(id);
        JsonObject schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        Random random = new(SettingsGenerators.Seed + (int)id);

        foreach (Variant variant in Enum.GetValues<Variant>())
        {
            for (int round = 0; round < 8; round++)
            {
                JsonNode generated = Generate(schema, variant, random);
                Assert.True(
                    Accepts(tool, generated),
                    $"{tool.Name} rejected its own {variant} argument set {generated.ToJsonString()}");
            }
        }
    }

    /// <summary>Verifies removing each required argument, adding an unknown one, or presenting a non-object is rejected.</summary>
    /// <param name="id">Tool under test.</param>
    [Theory]
    [MemberData(nameof(Tools))]
    public void StructuralViolations_AreRejected(McpToolId id)
    {
        McpToolDefinition tool = Definition(id);
        JsonObject schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        JsonObject valid = Generate(schema, Variant.Maximal, new Random(SettingsGenerators.Seed)).AsObject();
        Assert.True(Accepts(tool, valid));

        foreach (string required in schema["required"]?.AsArray().Select(static node => node!.GetValue<string>()) ?? [])
        {
            JsonObject without = Clone(valid);
            without.Remove(required);
            Assert.False(Accepts(tool, without), $"{tool.Name} accepted arguments without '{required}'");
        }

        JsonObject extra = Clone(valid);
        extra["unexpected_argument"] = 1;
        Assert.False(Accepts(tool, extra));
        JsonObject lookalike = Clone(valid);
        lookalike["Operation_Id"] = "x";
        Assert.False(Accepts(tool, lookalike));

        foreach (string nonObject in new[] { "null", "[]", "\"x\"", "1", "true", "[{}]" })
        {
            using JsonDocument document = JsonDocument.Parse(nonObject);
            Assert.False(McpToolArgumentValidator.IsValid(tool, document.RootElement), nonObject);
        }

        if (valid.Count > 0)
        {
            using JsonDocument duplicate = JsonDocument.Parse(DuplicateKey(valid));
            Assert.False(McpToolArgumentValidator.IsValid(tool, duplicate.RootElement), "duplicate member names must be rejected");
        }
    }

    /// <summary>Verifies each property rejects every wrong JSON type and each bound rejects its first out-of-range value.</summary>
    /// <param name="id">Tool under test.</param>
    [Theory]
    [MemberData(nameof(Tools))]
    public void PropertyViolations_AreRejected(McpToolId id)
    {
        McpToolDefinition tool = Definition(id);
        JsonObject schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        JsonObject valid = Generate(schema, Variant.Maximal, new Random(SettingsGenerators.Seed)).AsObject();
        int mutants = 0;

        foreach ((string name, JsonNode? propertySchemaNode) in schema["properties"]!.AsObject())
        {
            JsonObject propertySchema = propertySchemaNode!.AsObject();
            foreach (JsonNode? wrong in WrongTypes(propertySchema))
            {
                JsonObject mutated = Clone(valid);
                mutated[name] = wrong?.DeepClone();
                Assert.False(Accepts(tool, mutated), $"{tool.Name}.{name} accepted {wrong?.ToJsonString() ?? "null"}");
                mutants++;
            }

            foreach (JsonNode boundary in BoundaryViolations(propertySchema))
            {
                JsonObject mutated = Clone(valid);
                mutated[name] = boundary.DeepClone();
                string preview = boundary.ToJsonString();
                Assert.False(Accepts(tool, mutated), $"{tool.Name}.{name} accepted out-of-bounds {preview[..Math.Min(preview.Length, 80)]}");
                mutants++;
            }
        }

        Assert.True(mutants > 0 || schema["properties"]!.AsObject().Count == 0);
    }

    /// <summary>Verifies integer arguments accept exactly canonical 32-bit integers and reject every other numeric spelling.</summary>
    [Fact]
    public void IntegerArguments_RejectNonIntegerSpellingsAndOverflow()
    {
        McpToolDefinition tool = Definition(McpToolId.ListProfiles);
        string[] rejected = ["1.0", "1.5", "1e0", "1E1", "10000.0000000001", "2147483648", "-2147483649", "-1", "10001", "\"1\"", "true", "null", "[1]", "{}", "9223372036854775807"];
        foreach (string text in rejected)
        {
            Assert.False(Accepts(tool, JsonNode.Parse($"{{\"limit\":{text}}}")!), $"limit={text}");
            Assert.False(Accepts(tool, JsonNode.Parse($"{{\"offset\":{text}}}")!), $"offset={text}");
        }

        foreach (int accepted in new[] { 1, 25, 100 })
        {
            Assert.True(Accepts(tool, JsonNode.Parse($"{{\"limit\":{accepted}}}")!));
        }

        Assert.True(Accepts(tool, JsonNode.Parse("{\"offset\":0,\"limit\":100}")!));
        Assert.False(Accepts(tool, JsonNode.Parse("{\"limit\":0}")!));
        Assert.False(Accepts(tool, JsonNode.Parse("{\"limit\":101}")!));
        Assert.True(Accepts(tool, JsonNode.Parse("{\"offset\":10000}")!));
        Assert.False(Accepts(tool, JsonNode.Parse("{\"offset\":10001}")!));
    }

    /// <summary>Verifies pattern-constrained strings are matched as whole values: no trailing line break, no embedded extra lines.</summary>
    [Fact]
    public void PatternArguments_AreMatchedAsWholeValues()
    {
        McpToolDefinition logRead = Definition(McpToolId.ReadLogHistory);
        string valid = PatternSamples["^[0-9a-f]{32}:[1-9][0-9]{0,18}$"];
        Assert.True(Accepts(logRead, new JsonObject { ["record_id"] = valid }));
        foreach (string malformed in new[] { valid + "\n", valid + "\r\n", "\n" + valid, valid + " ", valid + "\u0000", valid[..^1] + "٣", valid.ToUpperInvariant(), valid.Replace(":", ";", StringComparison.Ordinal) })
        {
            Assert.False(Accepts(logRead, new JsonObject { ["record_id"] = malformed }), $"record_id {malformed.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)}");
        }

        McpToolDefinition save = Definition(McpToolId.SaveProfile);
        JsonObject saveArguments = Generate(JsonNode.Parse(save.InputSchema.GetRawText())!.AsObject(), Variant.Maximal, new Random(1)).AsObject();
        string hash = PatternSamples["^[a-fA-F0-9]{64}$"];
        saveArguments["expected_sha256"] = hash;
        Assert.True(Accepts(save, saveArguments));
        foreach (string malformed in new[] { hash + "\n", hash + "\r\n", hash[..63], hash + "0", hash[..63] + "g", hash[..63] + "٣" })
        {
            saveArguments["expected_sha256"] = malformed;
            Assert.False(Accepts(save, saveArguments), "expected_sha256 " + malformed.Length);
        }
    }

    /// <summary>Verifies UUID-formatted arguments reject the empty identity and non-canonical text.</summary>
    [Fact]
    public void UuidArguments_RejectEmptyAndMalformedIdentities()
    {
        McpToolDefinition tool = Definition(McpToolId.RetrySettingsApplication);
        JsonObject valid = Generate(JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject(), Variant.Maximal, new Random(2)).AsObject();
        Assert.True(Accepts(tool, valid));
        foreach (string field in new[] { "operation_id", "batch_id", "attempt_id" })
        {
            foreach (string bad in new[] { Guid.Empty.ToString("D"), "not-a-guid", string.Empty, " ", Guid.NewGuid().ToString("D")[..35], "00000000-0000-0000-0000-000000000000", "{00000000-0000-0000-0000-000000000000}" })
            {
                JsonObject mutated = Clone(valid);
                mutated[field] = bad;
                Assert.False(Accepts(tool, mutated), $"{field}={bad}");
            }
        }
    }

    /// <summary>Verifies enum arguments are exact and case-sensitive.</summary>
    [Fact]
    public void EnumArguments_AreExactAndCaseSensitive()
    {
        McpToolDefinition tool = Definition(McpToolId.SetProxyState);
        foreach (string mode in new[] { "Disabled", "Standby", "RuleTakeover", "FullTakeover" })
        {
            Assert.True(Accepts(tool, new JsonObject { ["operation_id"] = Guid.NewGuid().ToString(), ["mode"] = mode }));
            foreach (string variant in new[] { mode.ToUpperInvariant(), mode.ToLowerInvariant(), mode + " ", " " + mode, mode[..^1], string.Empty })
            {
                Assert.False(Accepts(tool, new JsonObject { ["operation_id"] = Guid.NewGuid().ToString(), ["mode"] = variant }), variant);
            }
        }

        Assert.False(Accepts(tool, new JsonObject { ["operation_id"] = Guid.NewGuid().ToString(), ["mode"] = 2 }));
        Assert.False(Accepts(tool, new JsonObject { ["operation_id"] = Guid.NewGuid().ToString(), ["mode"] = "Faulted" }));
    }

    private static McpToolDefinition Definition(McpToolId id) => McpToolCatalog.All.Single(tool => tool.Id == id);

    private static bool Accepts(McpToolDefinition tool, JsonNode arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments.ToJsonString());
        return McpToolArgumentValidator.IsValid(tool, document.RootElement);
    }

    private static JsonObject Clone(JsonObject source) => (JsonObject)source.DeepClone();

    private static string DuplicateKey(JsonObject valid)
    {
        string text = valid.ToJsonString();
        string firstName = valid.First().Key;
        return "{\"" + firstName + "\":" + valid[firstName]!.ToJsonString() + "," + text[1..];
    }

    private enum Variant
    {
        Minimal,
        Maximal,
        Random,
    }

    private static JsonNode Generate(JsonObject schema, Variant variant, Random random)
    {
        if (schema.TryGetPropertyValue("enum", out JsonNode? options))
        {
            JsonArray choices = options!.AsArray();
            return choices[variant == Variant.Random ? random.Next(choices.Count) : 0]!.DeepClone();
        }

        switch (schema["type"]!.GetValue<string>())
        {
            case "object":
                JsonObject result = [];
                HashSet<string> required = [.. schema["required"]?.AsArray().Select(static node => node!.GetValue<string>()) ?? []];
                if (schema.TryGetPropertyValue("properties", out JsonNode? properties))
                {
                    foreach ((string name, JsonNode? child) in properties!.AsObject())
                    {
                        bool include = required.Contains(name) || variant == Variant.Maximal || (variant == Variant.Random && random.Next(2) == 0);
                        if (include)
                        {
                            result[name] = Generate(child!.AsObject(), variant, random);
                        }
                    }
                }
                else
                {
                    int minimum = schema["minProperties"]?.GetValue<int>() ?? 0;
                    int maximum = schema["maxProperties"]?.GetValue<int>() ?? 4;
                    int count = variant switch { Variant.Minimal => minimum, Variant.Maximal => maximum, _ => random.Next(minimum, maximum + 1) };
                    for (int index = 0; index < count; index++)
                    {
                        result["k" + index] = index;
                    }
                }

                return result;
            case "array":
                JsonArray array = [];
                int minItems = schema["minItems"]?.GetValue<int>() ?? 0;
                int maxItems = schema["maxItems"]?.GetValue<int>() ?? 3;
                int items = variant switch { Variant.Minimal => minItems, Variant.Maximal => maxItems, _ => random.Next(minItems, maxItems + 1) };
                for (int index = 0; index < items; index++)
                {
                    array.Add(Generate(schema["items"]!.AsObject(), variant, random));
                }

                return array;
            case "integer":
                int low = schema["minimum"]?.GetValue<int>() ?? 0;
                int high = schema["maximum"]?.GetValue<int>() ?? 1000;
                return variant switch { Variant.Minimal => low, Variant.Maximal => high, _ => random.Next(low, high + 1) };
            case "string":
                return GenerateString(schema, variant, random);
            default:
                throw new NotSupportedException("Unclassified schema type " + schema["type"]);
        }
    }

    private static JsonNode GenerateString(JsonObject schema, Variant variant, Random random)
    {
        if (schema.TryGetPropertyValue("format", out JsonNode? format))
        {
            Assert.Equal("uuid", format!.GetValue<string>());
            return Guid.NewGuid().ToString("D");
        }

        if (schema.TryGetPropertyValue("pattern", out JsonNode? pattern))
        {
            Assert.True(PatternSamples.TryGetValue(pattern!.GetValue<string>(), out string? sample), "Add a sample generator for the new pattern " + pattern);
            return sample;
        }

        int minimum = schema["minLength"]?.GetValue<int>() ?? 0;
        int maximum = Math.Min(schema["maxLength"]?.GetValue<int>() ?? 64, 1_048_576);
        int length = variant switch { Variant.Minimal => minimum, Variant.Maximal => maximum, _ => random.Next(minimum, Math.Min(maximum, 300) + 1) };
        return new string('a', length);
    }

    private static IEnumerable<JsonNode?> WrongTypes(JsonObject schema)
    {
        string type = schema.TryGetPropertyValue("enum", out _) ? "string" : schema["type"]!.GetValue<string>();
        yield return null;
        if (type != "string")
        {
            yield return JsonValue.Create("x");
        }

        if (type is not "integer")
        {
            yield return JsonValue.Create(1);
        }

        yield return JsonValue.Create(true);
        yield return JsonValue.Create(1.5);
        if (type != "array")
        {
            yield return new JsonArray();
        }

        if (type != "object")
        {
            yield return new JsonObject();
        }
    }

    private static IEnumerable<JsonNode> BoundaryViolations(JsonObject schema)
    {
        if (schema.TryGetPropertyValue("enum", out JsonNode? options))
        {
            yield return JsonValue.Create("definitely-not-allowed")!;
            yield return JsonValue.Create(options!.AsArray()[0]!.GetValue<string>().ToUpperInvariant() + "x")!;
        }

        switch (schema["type"]!.GetValue<string>())
        {
            case "integer":
                if (schema["minimum"] is JsonNode minimum)
                {
                    yield return JsonValue.Create(minimum.GetValue<int>() - 1)!;
                }

                if (schema["maximum"] is JsonNode maximum)
                {
                    yield return JsonValue.Create(maximum.GetValue<int>() + 1)!;
                }

                yield return JsonValue.Create(int.MinValue)!;
                yield return JsonNode.Parse("2147483648")!;
                break;
            case "string":
                if (schema["minLength"] is JsonNode minLength && minLength.GetValue<int>() > 0)
                {
                    yield return JsonValue.Create(new string('a', minLength.GetValue<int>() - 1))!;
                }

                if (schema["maxLength"] is JsonNode maxLength)
                {
                    yield return JsonValue.Create(new string('a', maxLength.GetValue<int>() + 1))!;
                }

                break;
            case "array":
                if (schema["maxItems"] is JsonNode maxItems)
                {
                    yield return new JsonArray([.. Enumerable.Range(0, maxItems.GetValue<int>() + 1).Select(static _ => (JsonNode?)JsonValue.Create("a"))]);
                }

                break;
            case "object":
                if (schema["minProperties"] is JsonNode minProperties && minProperties.GetValue<int>() > 0)
                {
                    yield return new JsonObject();
                }

                if (schema["maxProperties"] is JsonNode maxProperties)
                {
                    JsonObject over = [];
                    for (int index = 0; index <= maxProperties.GetValue<int>(); index++)
                    {
                        over["k" + index] = index;
                    }

                    yield return over;
                }

                break;
        }
    }
}

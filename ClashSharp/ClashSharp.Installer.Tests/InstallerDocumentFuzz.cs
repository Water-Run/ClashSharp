using System.Text;
using System.Text.Json;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

/// <summary>Families of damage applied to a canonical on-disk or on-wire document.</summary>
public enum MutationFamily
{
    /// <summary>Every proper prefix, including the empty document.</summary>
    Truncation,

    /// <summary>Every (or a seeded sample of) single-bit flip.</summary>
    BitFlip,

    /// <summary>Every single-byte deletion.</summary>
    ByteDeletion,

    /// <summary>Seeded insertion of structurally interesting bytes.</summary>
    ByteInsertion,

    /// <summary>BOM, CRLF, indentation, alternate encodings, trailing and duplicated content.</summary>
    Envelope,

    /// <summary>Seeded multi-edit corruption.</summary>
    RandomEdits,

    /// <summary>Unknown, duplicated, removed, reordered or retyped properties.</summary>
    Structural,

    /// <summary>Documents beyond the declared size bound.</summary>
    Oversize,
}

/// <summary>Describes one canonical document format and how to parse and re-emit it.</summary>
/// <param name="Name">Stable display key.</param>
/// <param name="Samples">Canonical documents covering the format's variants.</param>
/// <param name="Parse">The production parser.</param>
/// <param name="Serialize">The production serializer for an object returned by <paramref name="Parse"/>.</param>
/// <param name="StrictCanonical">Whether any accepted input must equal its re-serialization byte for byte.</param>
/// <param name="MaximumBytes">The declared document size bound.</param>
internal sealed record DocumentSpec(
    string Name,
    Func<IReadOnlyList<byte[]>> Samples,
    Func<byte[], object> Parse,
    Func<object, byte[]> Serialize,
    bool StrictCanonical,
    int MaximumBytes);

internal static class DocumentFuzz
{
    private const int Seed = 20261109;

    /// <summary>Applies one family to every sample and asserts the parse contract on each mutant.</summary>
    internal static int Run(DocumentSpec spec, MutationFamily family)
    {
        int checkedCount = 0;
        foreach (byte[] sample in spec.Samples())
        {
            // The pristine document is the oracle for everything below.
            object parsed = spec.Parse(sample);
            Assert.Equal(sample, spec.Serialize(parsed));
            Assert.True(sample.Length <= spec.MaximumBytes, "A canonical sample exceeds the declared bound.");
            foreach ((string label, byte[] mutant) in Mutants(spec, sample, family))
            {
                AssertContract(spec, mutant, $"{spec.Name}/{family}/{label}");
                checkedCount++;
            }
        }

        Assert.True(checkedCount > 0);
        return checkedCount;
    }

    /// <summary>
    /// A parser may accept an input only when it is a valid document (and, for strict formats, the
    /// canonical one); every refusal must be the declared stable exception, never anything else.
    /// </summary>
    internal static void AssertContract(DocumentSpec spec, byte[] input, string label)
    {
        object? parsed = null;
        try
        {
            parsed = spec.Parse(input);
        }
        catch (InstallerProtocolException exception)
        {
            try
            {
                InstallerProtocolValidation.ValidateDiagnosticCode(exception.DiagnosticCode);
            }
            catch (InstallerProtocolException)
            {
                Assert.Fail($"{label}: refusal code '{exception.DiagnosticCode}' is not a stable diagnostic code.");
            }

            Assert.Equal(exception.DiagnosticCode, exception.Message);
            return;
        }
        catch (Exception exception)
        {
            Assert.Fail($"{label}: undeclared {exception.GetType().FullName}: {exception.Message}");
        }

        Assert.NotNull(parsed);
        byte[] reserialized = spec.Serialize(parsed);
        if (spec.StrictCanonical)
        {
            Assert.True(
                input.AsSpan().SequenceEqual(reserialized),
                $"{label}: a strict codec accepted a non-canonical document.");
        }
        else
        {
            // Accepted but possibly spelled differently: the meaning must be stable under re-emit.
            object again = spec.Parse(reserialized);
            Assert.Equal(reserialized, spec.Serialize(again));
        }
    }

    internal static IEnumerable<(string Label, byte[] Bytes)> Mutants(
        DocumentSpec spec,
        byte[] canonical,
        MutationFamily family)
    {
        Random random = new(Seed ^ canonical.Length ^ (int)family);
        switch (family)
        {
            case MutationFamily.Truncation:
                for (int length = 0; length < canonical.Length; length++)
                {
                    yield return ($"truncate@{length}", canonical[..length]);
                }

                break;
            case MutationFamily.BitFlip:
                int bits = canonical.Length * 8;
                int budget = canonical.Length > 512 ? 3000 : bits;
                IEnumerable<int> positions = budget >= bits
                    ? Enumerable.Range(0, bits)
                    : Enumerable.Range(0, budget).Select(_ => random.Next(bits)).Distinct();
                foreach (int bit in positions)
                {
                    byte[] flipped = (byte[])canonical.Clone();
                    flipped[bit / 8] ^= (byte)(1 << (bit % 8));
                    yield return ($"flip@{bit}", flipped);
                }

                break;
            case MutationFamily.ByteDeletion:
                for (int index = 0; index < canonical.Length; index++)
                {
                    yield return ($"delete@{index}", [.. canonical[..index], .. canonical[(index + 1)..]]);
                }

                break;
            case MutationFamily.ByteInsertion:
                byte[] interesting = [0x00, 0x7F, 0x80, 0xFF, (byte)'"', (byte)'\\', (byte)'{', (byte)'}', (byte)',', (byte)' ', (byte)'\n'];
                for (int attempt = 0; attempt < 1000; attempt++)
                {
                    int at = random.Next(canonical.Length + 1);
                    byte value = interesting[random.Next(interesting.Length)];
                    yield return ($"insert{value:X2}@{at}", [.. canonical[..at], value, .. canonical[at..]]);
                }

                break;
            case MutationFamily.Envelope:
                foreach ((string label, byte[] bytes) in EnvelopeVariants(canonical))
                {
                    yield return (label, bytes);
                }

                break;
            case MutationFamily.RandomEdits:
                for (int attempt = 0; attempt < 1000; attempt++)
                {
                    byte[] edited = (byte[])canonical.Clone();
                    int edits = random.Next(2, 6);
                    for (int edit = 0; edit < edits; edit++)
                    {
                        switch (random.Next(4))
                        {
                            case 0:
                                edited[random.Next(edited.Length)] = (byte)random.Next(256);
                                break;
                            case 1:
                                int a = random.Next(edited.Length);
                                int b = random.Next(edited.Length);
                                (edited[a], edited[b]) = (edited[b], edited[a]);
                                break;
                            case 2:
                                int start = random.Next(edited.Length);
                                int length = random.Next(1, Math.Min(16, edited.Length - start) + 1);
                                random.NextBytes(edited.AsSpan(start, length));
                                break;
                            default:
                                int from = random.Next(edited.Length);
                                int to = random.Next(edited.Length);
                                edited[to] = edited[from];
                                break;
                        }
                    }

                    yield return ($"random#{attempt}", edited);
                }

                break;
            case MutationFamily.Structural:
                foreach ((string label, byte[] bytes) in StructuralVariants(canonical))
                {
                    yield return (label, bytes);
                }

                break;
            default:
                yield return ("one-over", Padded(canonical, spec.MaximumBytes + 1));
                yield return ("double", Padded(canonical, spec.MaximumBytes * 2));
                yield return ("one-mebibyte", Padded(canonical, 1024 * 1024));
                yield return ("max-exactly-padded", Padded(canonical, spec.MaximumBytes));
                break;
        }
    }

    private static byte[] Padded(byte[] canonical, int length)
    {
        // Syntactically plausible trailing content that a lenient parser might skip.
        byte[] bytes = new byte[length];
        for (int index = 0; index < length; index++)
        {
            bytes[index] = index < canonical.Length ? canonical[index] : (byte)' ';
        }

        return bytes;
    }

    private static IEnumerable<(string, byte[])> EnvelopeVariants(byte[] canonical)
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        yield return ("utf8-bom", [.. bom, .. canonical]);
        yield return ("trailing-lf", [.. canonical, (byte)'\n']);
        yield return ("trailing-crlf", [.. canonical, (byte)'\r', (byte)'\n']);
        yield return ("trailing-space", [.. canonical, (byte)' ']);
        yield return ("trailing-nul", [.. canonical, 0]);
        yield return ("leading-space", [(byte)' ', .. canonical]);
        yield return ("leading-newline", [(byte)'\n', .. canonical]);
        yield return ("trailing-comma-garbage", [.. canonical, (byte)',']);
        yield return ("doubled", [.. canonical, .. canonical]);
        yield return ("doubled-with-newline", [.. canonical, (byte)'\n', .. canonical]);
        yield return ("array-wrapped", [(byte)'[', .. canonical, (byte)']']);
        yield return ("string-wrapped", [(byte)'"', .. canonical, (byte)'"']);
        string text = Encoding.UTF8.GetString(canonical);
        yield return ("utf16le", Encoding.Unicode.GetBytes(text));
        yield return ("utf16le-bom", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]);
        yield return ("utf16be", Encoding.BigEndianUnicode.GetBytes(text));
        yield return ("utf32", Encoding.UTF32.GetBytes(text));
        yield return ("latin1-lossy", Encoding.Latin1.GetBytes(text.Replace("\"", "\"é", StringComparison.Ordinal)));
        yield return ("comment-prefixed", [.. "/*x*/"u8.ToArray(), .. canonical]);
        yield return ("line-comment-suffixed", [.. canonical, .. "//x"u8.ToArray()]);
        yield return ("single-quoted", Encoding.UTF8.GetBytes(text.Replace('"', '\'')));
        yield return ("nan-token", Encoding.UTF8.GetBytes(text.Replace(":1,", ":NaN,", StringComparison.Ordinal)));
        yield return ("only-whitespace", "   "u8.ToArray());
        yield return ("null-literal", "null"u8.ToArray());
        yield return ("empty-object", "{}"u8.ToArray());
        yield return ("empty-array", "[]"u8.ToArray());
        foreach (string newLine in new[] { "\n", "\r\n" })
        {
            using JsonDocument document = JsonDocument.Parse(canonical);
            using MemoryStream indented = new();
            using (Utf8JsonWriter writer = new(indented, new JsonWriterOptions { Indented = true, NewLine = newLine }))
            {
                document.WriteTo(writer);
            }

            yield return ($"indented-{(newLine == "\n" ? "lf" : "crlf")}", indented.ToArray());
        }

        // Alias a string's first character by an escape: semantically equal, byte-wise different.
        string aliased = AliasFirstStringCharacter(text);
        if (aliased != text)
        {
            yield return ("escaped-alias", Encoding.UTF8.GetBytes(aliased));
        }
    }

    private static string AliasFirstStringCharacter(string text)
    {
        // Skip the first quoted token (a property name) and alias the first character of a value.
        int index = text.IndexOf(":\"", StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }

        int at = index + 2;
        return string.Concat(text.AsSpan(0, at), $"\\u{(int)text[at]:x4}", text.AsSpan(at + 1));
    }

    private static IEnumerable<(string, byte[])> StructuralVariants(byte[] canonical)
    {
        List<(string Name, string Raw)> properties = ReadTopLevel(canonical);
        string[] replacements = ["null", "0", "-1", "1.5", "99999999999999999999", "\"\"", "\"x\"", "true", "false", "[]", "{}", "[1]", "{\"a\":1}"];
        for (int index = 0; index <= properties.Count; index++)
        {
            var inserted = properties.ToList();
            inserted.Insert(index, ("zzUnknown", "true"));
            yield return ($"unknown-property@{index}", Emit(inserted));
        }

        for (int index = 0; index < properties.Count; index++)
        {
            var duplicated = properties.ToList();
            duplicated.Insert(index + 1, properties[index]);
            yield return ($"duplicate-{properties[index].Name}", Emit(duplicated));

            var shadowed = properties.ToList();
            shadowed.Insert(index + 1, (properties[index].Name, "null"));
            yield return ($"shadow-{properties[index].Name}", Emit(shadowed));

            var removed = properties.ToList();
            removed.RemoveAt(index);
            yield return ($"remove-{properties[index].Name}", Emit(removed));

            var caseChanged = properties.ToList();
            caseChanged[index] = (char.ToUpperInvariant(properties[index].Name[0]) + properties[index].Name[1..], properties[index].Raw);
            yield return ($"case-{properties[index].Name}", Emit(caseChanged));

            foreach (string replacement in replacements.Where(value => value != properties[index].Raw))
            {
                var retyped = properties.ToList();
                retyped[index] = (properties[index].Name, replacement);
                yield return ($"retype-{properties[index].Name}={replacement}", Emit(retyped));
            }

            if (index + 1 < properties.Count)
            {
                var swapped = properties.ToList();
                (swapped[index], swapped[index + 1]) = (swapped[index + 1], swapped[index]);
                yield return ($"reorder-{properties[index].Name}", Emit(swapped));
            }
        }
    }

    private static List<(string Name, string Raw)> ReadTopLevel(byte[] canonical)
    {
        using JsonDocument document = JsonDocument.Parse(canonical);
        return document.RootElement.EnumerateObject()
            .Select(static property => (property.Name, property.Value.GetRawText()))
            .ToList();
    }

    private static byte[] Emit(IEnumerable<(string Name, string Raw)> properties) =>
        Encoding.UTF8.GetBytes("{" + string.Join(",", properties.Select(static property =>
            $"\"{property.Name}\":{property.Raw}")) + "}");
}

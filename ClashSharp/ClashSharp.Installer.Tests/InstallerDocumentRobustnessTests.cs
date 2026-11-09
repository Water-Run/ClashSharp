using System.Diagnostics;
using System.Text;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// Every persisted or on-wire installer document: canonical identity, and refusal of damaged
/// input with the declared exception only, within bounded work and allocation.
/// </summary>
public sealed class InstallerDocumentRobustnessTests
{
    public static TheoryData<string> AllDocuments() => new(DocumentSpecs.Names);

    public static TheoryData<string, MutationFamily> AllDocumentsAndFamilies()
    {
        var data = new TheoryData<string, MutationFamily>();
        foreach (string name in DocumentSpecs.Names)
        {
            foreach (MutationFamily family in Enum.GetValues<MutationFamily>())
            {
                data.Add(name, family);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllDocumentsAndFamilies))]
    public void DamagedInputIsRefusedWithTheDeclaredExceptionOnly(string document, MutationFamily family) =>
        DocumentFuzz.Run(DocumentSpecs.Get(document), family);

    [Theory]
    [MemberData(nameof(AllDocuments))]
    public void CanonicalDocumentsRoundTripByteForByteAndParsingIsDeterministic(string document)
    {
        DocumentSpec spec = DocumentSpecs.Get(document);
        foreach (byte[] sample in spec.Samples())
        {
            object first = spec.Parse(sample);
            object second = spec.Parse((byte[])sample.Clone());

            Assert.Equal(sample, spec.Serialize(first));
            Assert.Equal(spec.Serialize(first), spec.Serialize(second));
            Assert.True(sample.Length <= spec.MaximumBytes);
        }
    }

    [Theory]
    [MemberData(nameof(AllDocuments))]
    public void SizeBoundIsEnforcedBeforeAnyWorkProportionalToTheInput(string document)
    {
        DocumentSpec spec = DocumentSpecs.Get(document);
        byte[] canonical = spec.Samples()[0];
        byte[] huge = new byte[Math.Max(8 * 1024 * 1024, spec.MaximumBytes * 4)];
        Array.Fill(huge, (byte)'{');
        canonical.CopyTo(huge, 0);

        // Warm the parser so one-time initialisation is not billed to the bounded path.
        _ = spec.Parse(canonical);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long ticks = Stopwatch.GetTimestamp();
        Exception? failure = Record.Exception(() => spec.Parse(huge));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TimeSpan elapsed = Stopwatch.GetElapsedTime(ticks);

        InstallerProtocolException refusal = Assert.IsType<InstallerProtocolException>(failure);
        Assert.Contains("size", refusal.DiagnosticCode, StringComparison.Ordinal);
        Assert.True(
            allocated < 64 * 1024 + (spec.Name.EndsWith("frame", StringComparison.Ordinal) ? huge.Length + 1024 : 0),
            $"{document}: {allocated} bytes were allocated to refuse an oversize input.");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"{document}: refusal took {elapsed}.");
    }

    [Theory]
    [MemberData(nameof(AllDocuments))]
    public void AdversarialShapesWithinTheSizeBoundAreRefusedWithBoundedWork(string document)
    {
        DocumentSpec spec = DocumentSpecs.Get(document);
        // Frame formats put the bound in the length prefix; raw formats in the payload itself.
        int budget = Math.Min(spec.MaximumBytes - 2, 60 * 1024);
        string nestedArrays = new string('[', budget / 2) + new string(']', budget / 2);
        string nestedObjects = string.Concat(Enumerable.Repeat("{\"a\":", budget / 8))
            + "1" + new string('}', budget / 8);
        string hugeString = "{\"schema\":\"" + new string('a', budget - 20) + "\"}";
        string hugeNumber = "{\"schema\":" + new string('9', budget - 20) + "}";
        string manyProperties = "{" + string.Join(",", Enumerable.Range(0, budget / 12).Select(static i => $"\"p{i}\":1")) + "}";
        string manyElements = "[" + string.Join(",", Enumerable.Repeat("0", budget / 2)) + "]";
        string unpairedSurrogate = "{\"schema\":\"\\ud800\"}";
        string deepRawControl = "{\"schema\":\"\u0001\u0002\"}";
        foreach ((string label, string json) in new[]
        {
            ("nested-arrays", nestedArrays),
            ("nested-objects", nestedObjects),
            ("huge-string", hugeString),
            ("huge-number", hugeNumber),
            ("many-properties", manyProperties),
            ("many-elements", manyElements),
            ("unpaired-surrogate-escape", unpairedSurrogate),
            ("raw-control-characters", deepRawControl),
        })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long ticks = Stopwatch.GetTimestamp();

            DocumentFuzz.AssertContract(spec, bytes, $"{document}/{label}");

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(
                allocated < 64L * 1024 * 1024,
                $"{document}/{label}: allocated {allocated} bytes for a {bytes.Length}-byte input.");
            Assert.True(Stopwatch.GetElapsedTime(ticks) < TimeSpan.FromSeconds(5), $"{document}/{label} was slow.");
        }
    }

    [Theory]
    [MemberData(nameof(AllDocuments))]
    public void EveryRefusalIsStableAcrossRepeatedAttemptsOfTheSameBytes(string document)
    {
        DocumentSpec spec = DocumentSpecs.Get(document);
        byte[] canonical = spec.Samples()[0];
        Random random = new(20261109);
        for (int attempt = 0; attempt < 200; attempt++)
        {
            byte[] mutant = (byte[])canonical.Clone();
            mutant[random.Next(mutant.Length)] ^= (byte)(1 << random.Next(8));
            string? first = Outcome(spec, mutant);
            string? second = Outcome(spec, mutant);
            Assert.Equal(first, second);
        }
    }

    private static string? Outcome(DocumentSpec spec, byte[] bytes)
    {
        try
        {
            return $"ok:{Convert.ToHexString(spec.Serialize(spec.Parse(bytes)))}";
        }
        catch (InstallerProtocolException exception)
        {
            return exception.DiagnosticCode;
        }
    }
}

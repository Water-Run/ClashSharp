using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Property tests of the cleanup-ledger codec: the persisted document is canonical, so every byte string
/// either is exactly the canonical form of the ledger it parses to or is rejected with the one stable
/// protocol failure, at every truncation point and under every single-byte substitution.
/// </summary>
public sealed class WindowsInstallerDirectoryLedgerCodecPropertyTests
{
    private static readonly byte[] Substitutions = [0x00, 0x09, 0x0A, 0x0D, 0x20, 0x22, 0x2C, 0x30, 0x39, 0x3A, 0x41, 0x5C, 0x7B, 0x7D, 0xC3, 0xFF];

    [Fact]
    public void RandomLedgersRoundTripToIdenticalCanonicalBytes()
    {
        var random = new Random(20261109);
        InstallerDirectoryRole[] roles = Enum.GetValues<InstallerDirectoryRole>();
        for (int sample = 0; sample < 300; sample++)
        {
            InstallerDirectoryRole[] chosen = roles.Where(_ => random.Next(2) == 0).ToArray();
            WindowsInstallerOwnedDirectory[] directories = chosen
                .Select(role => new WindowsInstallerOwnedDirectory(
                    role,
                    new WindowsFileIdentity((uint)random.NextInt64(0, uint.MaxValue + 1L), (ulong)random.NextInt64(1, long.MaxValue))))
                .ToArray();
            InstallerTransactionSnapshot? terminal = random.Next(2) == 0 ? null : Terminal(random);
            var ledger = new WindowsInstallerDirectoryLedger(random.NextInt64(0, long.MaxValue), directories, terminal);

            byte[] bytes = WindowsInstallerDirectoryLedgerCodec.Serialize(ledger);
            WindowsInstallerDirectoryLedger parsed = WindowsInstallerDirectoryLedgerCodec.Parse(bytes);

            Assert.Equal(bytes, WindowsInstallerDirectoryLedgerCodec.Serialize(parsed));
            Assert.Equal(ledger.Generation, parsed.Generation);
            Assert.Equal(ledger.Directories, parsed.Directories);
            Assert.Equal(ledger.Terminal, parsed.Terminal);
            Assert.InRange(bytes.Length, 1, WindowsInstallerDirectoryLedgerCodec.MaximumDocumentBytes);
        }
    }

    [Fact]
    public void EveryTruncationOfEveryRepresentativeDocumentIsRejectedWithTheStableFailure()
    {
        foreach ((string label, byte[] bytes) in Documents())
        {
            _ = WindowsInstallerDirectoryLedgerCodec.Parse(bytes);
            for (int length = 0; length < bytes.Length; length++)
            {
                byte[] truncated = bytes[..length];

                Exception? failure = Record.Exception(() => WindowsInstallerDirectoryLedgerCodec.Parse(truncated));

                Assert.True(
                    failure is InstallerProtocolException { DiagnosticCode: string code } && code.StartsWith("installer.directory_ledger.", StringComparison.Ordinal),
                    $"{label} truncated to {length} bytes produced {failure?.GetType().Name ?? "success"}.");
            }
        }
    }

    [Fact]
    public void EverySingleByteSubstitutionIsRejectedOrIsItselfTheCanonicalFormOfWhatItParsesTo()
    {
        int accepted = 0;
        int rejected = 0;
        foreach ((string label, byte[] bytes) in Documents())
        {
            for (int offset = 0; offset < bytes.Length; offset++)
            {
                foreach (byte replacement in Substitutions)
                {
                    if (bytes[offset] == replacement)
                    {
                        continue;
                    }

                    byte[] mutated = (byte[])bytes.Clone();
                    mutated[offset] = replacement;
                    try
                    {
                        WindowsInstallerDirectoryLedger parsed = WindowsInstallerDirectoryLedgerCodec.Parse(mutated);
                        Assert.True(
                            mutated.AsSpan().SequenceEqual(WindowsInstallerDirectoryLedgerCodec.Serialize(parsed)),
                            $"{label}: byte {offset} -> 0x{replacement:X2} parsed without being canonical.");
                        accepted++;
                    }
                    catch (InstallerProtocolException)
                    {
                        rejected++;
                    }
                }
            }
        }

        Assert.True(rejected > accepted, "Almost every mutation must be rejected.");
    }

    [Fact]
    public void EncodingEnvelopesThatPreserveTheJsonValueAreStillNoncanonical()
    {
        byte[] canonical = WindowsInstallerDirectoryLedgerCodec.Serialize(Full());
        string text = System.Text.Encoding.UTF8.GetString(canonical);
        byte[][] variants =
        [
            [0xEF, 0xBB, 0xBF, .. canonical],
            [.. canonical, 0x0A],
            [.. canonical, 0x0D, 0x0A],
            [.. canonical, 0x20],
            System.Text.Encoding.UTF8.GetBytes(text.Replace(",", ", ", StringComparison.Ordinal)),
            System.Text.Encoding.UTF8.GetBytes(text.Replace("\"schema\"", "\"\\u0073chema\"", StringComparison.Ordinal)),
            System.Text.Encoding.UTF8.GetBytes(text.Replace("\"role\"", "\"Role\"", StringComparison.Ordinal)),
            System.Text.Encoding.Unicode.GetBytes(text),
            System.Text.Encoding.UTF32.GetBytes(text),
            System.Text.Encoding.UTF8.GetBytes(text.Replace("{", "{\n", StringComparison.Ordinal)),
        ];

        foreach (byte[] variant in variants)
        {
            Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerCodec.Parse(variant));
        }
    }

    [Fact]
    public void DocumentSizeBoundIsExactlyTheDeclaredMaximum()
    {
        byte[] canonical = WindowsInstallerDirectoryLedgerCodec.Serialize(Full());
        byte[] atLimit = new byte[WindowsInstallerDirectoryLedgerCodec.MaximumDocumentBytes];
        canonical.CopyTo(atLimit, 0);
        byte[] overLimit = new byte[atLimit.Length + 1];
        canonical.CopyTo(overLimit, 0);

        Assert.Equal(
            "installer.directory_ledger.document_size_invalid",
            Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerCodec.Parse(overLimit)).DiagnosticCode);
        Assert.Equal(
            "installer.directory_ledger.document_size_invalid",
            Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerCodec.Parse(Array.Empty<byte>())).DiagnosticCode);
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerCodec.Parse(atLimit));
    }

    private static IEnumerable<(string Label, byte[] Bytes)> Documents()
    {
        yield return ("empty", WindowsInstallerDirectoryLedgerCodec.Serialize(WindowsInstallerDirectoryLedger.Empty));
        yield return ("all roles", WindowsInstallerDirectoryLedgerCodec.Serialize(new WindowsInstallerDirectoryLedger(
            3,
            Enum.GetValues<InstallerDirectoryRole>().Select(static role => new WindowsInstallerOwnedDirectory(role, new WindowsFileIdentity(12, (ulong)role + 1))),
            null)));
        yield return ("with terminal", WindowsInstallerDirectoryLedgerCodec.Serialize(Full()));
    }

    private static WindowsInstallerDirectoryLedger Full() => new(
        9,
        Enum.GetValues<InstallerDirectoryRole>().Select(static role => new WindowsInstallerOwnedDirectory(role, new WindowsFileIdentity(12, (ulong)role + 1))),
        Terminal(new Random(1)));

    private static InstallerTransactionSnapshot Terminal(Random random)
    {
        string transaction = Convert.ToHexStringLower(Enumerable.Range(0, 32).Select(_ => (byte)random.Next(256)).ToArray());
        string payload = Convert.ToHexStringLower(Enumerable.Range(0, 32).Select(_ => (byte)random.Next(256)).ToArray());
        return InstallerTransactionSnapshot.Create(new InstallerTransactionJournal(
            InstallerTransactionJournal.CurrentSchema,
            transaction,
            InstallerOperation.Uninstall,
            "S-1-5-21-100-200-300-1001",
            false,
            "1.2.3.4",
            payload,
            InstallerTransactionPhase.Verified,
            5));
    }
}

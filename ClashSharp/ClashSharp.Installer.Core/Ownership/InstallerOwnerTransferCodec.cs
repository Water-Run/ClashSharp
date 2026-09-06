using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Ownership;

/// <summary>Encodes the bounded canonical private owner-transfer document.</summary>
public static class InstallerOwnerTransferCodec
{
    /// <summary>Gets the maximum UTF-8 document size, including both profiles and certificate ledgers.</summary>
    public const int MaximumDocumentBytes = 16 * 1024;

    /// <summary>Serializes validated private evidence; the caller owns the returned sensitive bytes.</summary>
    /// <param name="journal">Exact transfer evidence and durable progress.</param>
    public static byte[] Serialize(InstallerOwnerTransferJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            journal,
            InstallerOwnerTransferJsonContext.Default.InstallerOwnerTransferJournal);
        if (bytes.Length > MaximumDocumentBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InstallerProtocolException("installer.owner_transfer.document_size_invalid");
        }

        return bytes;
    }

    /// <summary>
    /// Parses one exact canonical document. Unknown or duplicate properties, omitted nullable
    /// properties, numeric enums, reordered fields, alternate escaping and whitespace are rejected.
    /// A valid document is evidence shape, never authorization to mutate Windows.
    /// </summary>
    /// <param name="bytes">Private UTF-8 input held by the elevated authority.</param>
    public static InstallerOwnerTransferJournal Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.owner_transfer.document_size_invalid");
        }

        try
        {
            InstallerOwnerTransferJournal journal = JsonSerializer.Deserialize(
                bytes,
                InstallerOwnerTransferJsonContext.Default.InstallerOwnerTransferJournal)
                ?? throw new JsonException("Private owner-transfer evidence must be an object.");
            journal.Validate();
            byte[] canonical = Serialize(journal);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(canonical, bytes))
                {
                    throw new JsonException("Private owner-transfer evidence is not canonical.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }

            return journal;
        }
        catch (JsonException)
        {
            // Do not retain parser details: they can contain private values or profile paths.
            throw new InstallerProtocolException("installer.owner_transfer.json_invalid");
        }
        catch (ArgumentException)
        {
            throw new InstallerProtocolException("installer.owner_transfer.json_invalid");
        }
    }
}

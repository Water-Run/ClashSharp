using System.Text;

namespace ClashSharp.Infrastructure.Triggers;

internal sealed class TriggerMigrationIntentStore
{
    private const string Header = "ClashSharp.TriggerMigration.v1";
    private const int MaximumIntentBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly string _path;

    public TriggerMigrationIntentStore(string legacyPath)
    {
        _path = legacyPath + ".migration-intent";
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: MaximumIntentBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumIntentBytes)
        {
            throw new InvalidDataException("The trigger migration intent exceeds its size limit.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The trigger migration intent is not valid UTF-8.", exception);
        }

        string[] lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 2
            || !StringComparer.Ordinal.Equals(lines[0], Header)
            || !IsSha256(lines[1]))
        {
            throw new InvalidDataException("The trigger migration intent is invalid.");
        }

        return lines[1];
    }

    public async Task EnsureAsync(string sourceHash, CancellationToken cancellationToken)
    {
        if (!IsSha256(sourceHash))
        {
            throw new ArgumentException("A lowercase SHA-256 hash is required.", nameof(sourceHash));
        }

        string? existing = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!StringComparer.Ordinal.Equals(existing, sourceHash))
            {
                throw new InvalidDataException(
                    "The trigger migration intent belongs to a different source.");
            }

            return;
        }

        string temporaryPath = _path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            byte[] content = Encoding.UTF8.GetBytes(Header + "\n" + sourceHash + "\n");
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, _path);
            }
            catch (IOException) when (File.Exists(_path))
            {
                string? concurrent = await ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(concurrent, sourceHash))
                {
                    throw new InvalidDataException(
                        "A concurrent trigger migration intent belongs to a different source.");
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64
            && value.All(char.IsAsciiHexDigitLower);
    }
}

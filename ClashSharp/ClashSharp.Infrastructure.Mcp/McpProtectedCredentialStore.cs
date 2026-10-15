using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Infrastructure.Agent;

/// <summary>Atomically stores one current-user DPAPI credential beneath its immutable data generation.</summary>
public sealed class McpProtectedCredentialStore : IMcpCredentialStore
{
    /// <summary>Fixed private credential filename, outside every exported proxy-configuration directory.</summary>
    public const string FileName = "mcp-credential.bin";
    private static readonly byte[] Magic = "ClashSharp.Mcp.Secret.v1\n"u8.ToArray();
    private const int MaximumCipherBytes = 16384;
    private readonly string _root;
    private readonly string _directory;
    private readonly string _path;
    private readonly Guid _generation;
    private readonly byte[] _entropy;
    private readonly RepositoryOperationLifetime _operations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _retirementGate = new();
    private Task? _retirement;

    /// <summary>Creates a generation-pinned store without opening files or creating directories.</summary>
    /// <param name="generation">Owner of the private credential; encrypted data cannot be transferred to another generation.</param>
    public McpProtectedCredentialStore(DataGenerationDescriptor generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        _root = generation.RootPath;
        _generation = generation.GenerationId;
        _directory = Path.Combine(_root, "Agent");
        _path = Path.Combine(_directory, FileName);
        _entropy = SHA256.HashData(Encoding.ASCII.GetBytes("ClashSharp.Mcp.Credential.v1/" + _generation.ToString("N")));
        _operations = new(this);
    }

    /// <inheritdoc />
    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        using IDisposable operation = _operations.Enter();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireDirectory(_root);
            if (!TryRequireDirectory(_directory)) { return null; }
            try { RequireOrdinaryFile(_path); }
            catch (FileNotFoundException) { return null; }
            await using FileStream stream = new(_path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
            });
            int headerSize = Magic.Length + 16 + sizeof(int);
            if (stream.Length < headerSize + 1 || stream.Length > headerSize + MaximumCipherBytes)
            {
                throw new InvalidDataException("The protected MCP credential has an invalid size.");
            }
            byte[] encoded = new byte[checked((int)stream.Length)];
            try
            {
                await stream.ReadExactlyAsync(encoded, cancellationToken).ConfigureAwait(false);
                if (!encoded.AsSpan(0, Magic.Length).SequenceEqual(Magic)
                    || new Guid(encoded.AsSpan(Magic.Length, 16)) != _generation)
                {
                    throw new InvalidDataException("The protected MCP credential has a different format or generation.");
                }
                int length = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(Magic.Length + 16, sizeof(int)));
                if (length != encoded.Length - headerSize) { throw new InvalidDataException("The protected MCP credential is truncated."); }
                byte[] cipher = encoded.AsSpan(headerSize).ToArray();
                byte[]? plain = null;
                try
                {
                    plain = ProtectedData.Unprotect(cipher, _entropy, DataProtectionScope.CurrentUser);
                    string secret = Encoding.ASCII.GetString(plain);
                    if (!McpAccessCredential.IsCanonicalSecret(secret)) { throw new InvalidDataException("The decrypted MCP credential is invalid."); }
                    return secret;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(cipher);
                    if (plain is not null) { CryptographicOperations.ZeroMemory(plain); }
                }
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task WriteAsync(string secret, CancellationToken cancellationToken)
    {
        if (!McpAccessCredential.IsCanonicalSecret(secret)) { throw new ArgumentException("Invalid MCP credential shape.", nameof(secret)); }
        using IDisposable operation = _operations.Enter();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        byte[] plain = Encoding.ASCII.GetBytes(secret);
        byte[]? cipher = null;
        byte[]? encoded = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDirectory(_root);
            if (!TryRequireDirectory(_directory)) { Directory.CreateDirectory(_directory); RequireDirectory(_directory); }
            if (File.Exists(_path) || Directory.Exists(_path)) { RequireOrdinaryFile(_path); }
            cipher = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);
            if (cipher.Length is < 1 or > MaximumCipherBytes) { throw new InvalidDataException("The protected MCP credential exceeds its byte budget."); }
            int headerSize = Magic.Length + 16 + sizeof(int);
            encoded = new byte[headerSize + cipher.Length];
            Magic.CopyTo(encoded, 0);
            _generation.TryWriteBytes(encoded.AsSpan(Magic.Length, 16));
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(Magic.Length + 16, sizeof(int)), cipher.Length);
            cipher.CopyTo(encoded, headerSize);
            temporary = Path.Combine(_directory, "credential-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (FileStream stream = new(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            }))
            {
                await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RequireDirectory(_directory);
            if (File.Exists(_path) || Directory.Exists(_path)) { RequireOrdinaryFile(_path); }
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            // After replacement succeeds, cancellation cannot turn a durable new key into a failed write.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (cipher is not null) { CryptographicOperations.ZeroMemory(cipher); }
            if (encoded is not null) { CryptographicOperations.ZeroMemory(encoded); }
            try { if (temporary is not null) { File.Delete(temporary); } }
            finally { _gate.Release(); }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_retirementGate) { return new(_retirement ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        await _operations.DisposeAsync().ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(_entropy);
        _gate.Dispose();
    }

    private static bool TryRequireDirectory(string path)
    {
        try { RequireDirectory(path); return true; }
        catch (DirectoryNotFoundException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    private static void RequireDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0) { throw new InvalidDataException("The MCP credential directory is not a directory."); }
        RequireNoReparseAncestors(path);
    }

    private static void RequireOrdinaryFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("The MCP credential is not an ordinary file.");
        }
        RequireNoReparseAncestors(Path.GetDirectoryName(path)!);
    }

    private static void RequireNoReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The MCP credential path contains a reparse point.");
            }
        }
    }
}

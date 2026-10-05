using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.Infrastructure.Settings;

/// <summary>Stores a bounded, device-local shell document through an atomic same-directory replacement.</summary>
public sealed class JsonWindowPlacementStore : IWindowPlacementStore, IDisposable
{
    private const int MaximumBytes = 4 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 4,
    };
    private readonly string _directory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a store under the existing application-local root without touching files.</summary>
    /// <param name="localDataRoot">Absolute application-local data root, independent of active data generations.</param>
    public JsonWindowPlacementStore(string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        if (!Path.IsPathFullyQualified(localDataRoot)) { throw new ArgumentException("An absolute local root is required.", nameof(localDataRoot)); }
        _directory = Path.Combine(Path.GetFullPath(localDataRoot), "WindowState", "v1");
        _path = Path.Combine(_directory, "placement.json");
    }

    /// <inheritdoc />
    public async Task<WindowPlacementState?> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidatePath(_path);
            if (!File.Exists(_path)) { return null; }
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is < 1 or > MaximumBytes) { throw new InvalidDataException("Invalid window-state document size."); }
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.ReadByte() != -1) { throw new InvalidDataException("Window-state document changed while reading."); }
            WindowPlacementState state = JsonSerializer.Deserialize<WindowPlacementState>(bytes, Options)
                ?? throw new InvalidDataException("Window-state document is empty.");
            state.Validate();
            if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(state, Options)))
            {
                throw new InvalidDataException("Window-state document is not canonical.");
            }
            return state;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task SaveAsync(WindowPlacementState state, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        if (bytes.Length > MaximumBytes) { throw new InvalidDataException("Window-state document is too large."); }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporary = Path.Combine(_directory, ".placement." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            ValidatePath(_path);
            Directory.CreateDirectory(_directory);
            ValidatePath(_path);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePath(_path);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) { File.Delete(temporary); } }
            finally { _gate.Release(); }
        }
    }

    private static void ValidatePath(string path)
    {
        string? current = path;
        while (current is not null)
        {
            FileAttributes? attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { attributes = null; }
            catch (DirectoryNotFoundException) { attributes = null; }
            if (attributes is { } existing && (existing & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Window-state paths must be ordinary filesystem entries.");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>Releases the I/O gate after its window owner drains initialization and checkpoint writes.</summary>
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _gate.Dispose();
    }
}

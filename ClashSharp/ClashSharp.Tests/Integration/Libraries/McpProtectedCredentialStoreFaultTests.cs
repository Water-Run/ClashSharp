using System.Security.Cryptography;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Agent;
using ClashSharp.Tests.Unit.Libraries;

namespace ClashSharp.Tests.Integration.Libraries;

/// <summary>
/// Fault properties of the DPAPI-protected MCP credential file: corruption at every byte offset is never reported as
/// absent nor accepted as a different secret, rotation and concurrent writers always leave one complete credential,
/// and cancelled or rejected writes leave the previous credential and no temporary files behind.
/// </summary>
public sealed class McpProtectedCredentialStoreFaultTests
{
    /// <summary>Verifies every single-byte corruption, truncation and extension is detected or yields the original secret only.</summary>
    [Fact]
    public async Task CorruptionAtEveryByteOffset_IsNeverAbsentAndNeverADifferentSecret()
    {
        using TestRoot root = new();
        await using McpProtectedCredentialStore store = new(root.Descriptor);
        string secret = McpAccessCredential.CreateSecret();
        await store.WriteAsync(secret, CancellationToken.None);
        string path = Path.Combine(root.Path, "Agent", McpProtectedCredentialStore.FileName);
        byte[] original = await File.ReadAllBytesAsync(path);
        int detected = 0;
        int tolerated = 0;

        async Task ProbeAsync(byte[] candidate, string description)
        {
            await File.WriteAllBytesAsync(path, candidate);
            try
            {
                string? read = await store.ReadAsync(CancellationToken.None);
                Assert.NotNull(read);
                Assert.True(read == secret, $"{description}: a different secret was accepted");
                tolerated++;
            }
            catch (Exception exception) when (exception is InvalidDataException or CryptographicException or IOException)
            {
                detected++;
            }
        }

        for (int offset = 0; offset < original.Length; offset++)
        {
            byte[] flipped = (byte[])original.Clone();
            flipped[offset] ^= 0x01;
            await ProbeAsync(flipped, $"bit flip at {offset}");
            byte[] zeroed = (byte[])original.Clone();
            zeroed[offset] = zeroed[offset] == 0 ? (byte)0xFF : (byte)0;
            await ProbeAsync(zeroed, $"overwrite at {offset}");
        }

        for (int length = 0; length < original.Length; length++)
        {
            await ProbeAsync(original[..length], $"truncated to {length}");
        }

        await ProbeAsync([.. original, 0], "extended by one byte");
        await ProbeAsync([.. original, .. original], "duplicated");
        await ProbeAsync(new byte[20_000], "oversized");
        Assert.True(detected > original.Length, "the vast majority of corruptions must be detected");
        Assert.True(tolerated < detected / 10, $"{tolerated} corruptions were tolerated");

        await File.WriteAllBytesAsync(path, original);
        Assert.Equal(secret, await store.ReadAsync(CancellationToken.None));
    }

    /// <summary>Verifies a sequence of rotations always leaves exactly the latest secret and no temporary files.</summary>
    [Fact]
    public async Task Rotation_AlwaysLeavesTheLatestSecretAndNoTemporaryFiles()
    {
        using TestRoot root = new();
        await using McpProtectedCredentialStore store = new(root.Descriptor);
        Assert.Null(await store.ReadAsync(CancellationToken.None));
        string last = string.Empty;
        for (int round = 0; round < 25; round++)
        {
            last = McpAccessCredential.CreateSecret();
            await store.WriteAsync(last, CancellationToken.None);

            Assert.Equal(last, await store.ReadAsync(CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root.Path, "Agent"), "*.tmp"));
            _ = Assert.Single(Directory.EnumerateFiles(Path.Combine(root.Path, "Agent")));
        }

        await using McpProtectedCredentialStore reopened = new(root.Descriptor);
        Assert.Equal(last, await reopened.ReadAsync(CancellationToken.None));
        byte[] file = await File.ReadAllBytesAsync(Path.Combine(root.Path, "Agent", McpProtectedCredentialStore.FileName));
        Assert.False(file.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(last)) >= 0, "the secret must not be stored in plaintext");
    }

    /// <summary>Verifies concurrent writers and readers always observe complete credentials from the written set.</summary>
    [Fact]
    public async Task ConcurrentWritersAndReaders_ObserveOnlyCompleteWrittenCredentials()
    {
        using TestRoot root = new();
        await using McpProtectedCredentialStore store = new(root.Descriptor);
        string[] secrets = [.. Enumerable.Range(0, 8).Select(static _ => McpAccessCredential.CreateSecret())];
        await store.WriteAsync(secrets[0], CancellationToken.None);
        using CancellationTokenSource stop = new();
        List<string> observed = [];

        Task readers = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                string? read = await store.ReadAsync(CancellationToken.None);
                lock (observed)
                {
                    observed.Add(read ?? "<absent>");
                }
            }
        });
        await Task.WhenAll(secrets.Select(secret => Task.Run(() => store.WriteAsync(secret, CancellationToken.None))));
        await stop.CancelAsync();
        await readers;

        lock (observed)
        {
            Assert.NotEmpty(observed);
            Assert.All(observed, value => Assert.Contains(value, secrets));
        }

        Assert.Contains(await store.ReadAsync(CancellationToken.None), secrets);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root.Path, "Agent"), "*.tmp"));
    }

    /// <summary>Verifies a cancelled or invalid write never alters the stored credential or leaves files behind.</summary>
    [Fact]
    public async Task CancelledOrInvalidWrites_LeaveThePreviousCredentialIntact()
    {
        using TestRoot root = new();
        await using McpProtectedCredentialStore store = new(root.Descriptor);
        string original = McpAccessCredential.CreateSecret();
        await store.WriteAsync(original, CancellationToken.None);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(McpAccessCredential.CreateSecret(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(cancelled.Token));
        foreach (string invalid in new[] { string.Empty, original.ToUpperInvariant(), original[..63], original + "0", " " + original[1..] })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(invalid, CancellationToken.None));
        }

        Assert.Equal(original, await store.ReadAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root.Path, "Agent"), "*.tmp"));
    }

    /// <summary>Verifies a store never creates files or directories merely by being read, and rejects use after disposal.</summary>
    [Fact]
    public async Task ReadOfAnAbsentCredential_HasNoSideEffectsAndDisposalEndsUse()
    {
        using TestRoot root = new();
        McpProtectedCredentialStore store = new(root.Descriptor);
        Assert.Null(await store.ReadAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));

        await store.DisposeAsync();
        await store.DisposeAsync();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => store.ReadAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => store.WriteAsync(McpAccessCredential.CreateSecret(), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    /// <summary>Verifies the credential bound to one generation cannot be read through another generation or relocated under a different identity.</summary>
    [Fact]
    public async Task CredentialIsBoundToItsGeneration()
    {
        using TestRoot first = new();
        using TestRoot second = new();
        string secret = McpAccessCredential.CreateSecret();
        await using (McpProtectedCredentialStore store = new(first.Descriptor))
        {
            await store.WriteAsync(secret, CancellationToken.None);
        }

        Directory.CreateDirectory(Path.Combine(second.Path, "Agent"));
        File.Copy(
            Path.Combine(first.Path, "Agent", McpProtectedCredentialStore.FileName),
            Path.Combine(second.Path, "Agent", McpProtectedCredentialStore.FileName));
        await using McpProtectedCredentialStore other = new(second.Descriptor);

        await Assert.ThrowsAsync<InvalidDataException>(() => other.ReadAsync(CancellationToken.None));
        Assert.NotEqual(first.Descriptor.GenerationId, second.Descriptor.GenerationId);
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClashSharp-McpStore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Descriptor = new DataGenerationDescriptor(Guid.NewGuid(), 1, Path);
        }

        public string Path { get; }

        public DataGenerationDescriptor Descriptor { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

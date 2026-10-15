using System.Security.Cryptography;
using System.Text;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Agent;

namespace ClashSharp.Tests.Unit.Agent;

/// <summary>Verifies actual Windows user protection and generation-bound credential-file behavior in isolated directories.</summary>
public sealed class McpProtectedCredentialStoreTests
{
    [Fact]
    public async Task MissingCredential_ReadDoesNotCreateFilesOrDirectories()
    {
        using Root root = new();
        await using McpProtectedCredentialStore store = new(root.Generation);
        Assert.Null(await store.ReadAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task ActualUserProtection_RoundTripsAndRotatesWithoutPlaintextOnDisk()
    {
        using Root root = new();
        string first = McpAccessCredential.CreateSecret();
        string replacement = McpAccessCredential.CreateSecret();
        await using (McpProtectedCredentialStore store = new(root.Generation))
        {
            await store.WriteAsync(first, CancellationToken.None);
            Assert.Equal(first, await store.ReadAsync(CancellationToken.None));
            byte[] bytes = await File.ReadAllBytesAsync(root.CredentialPath);
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(first)));
            await store.WriteAsync(replacement, CancellationToken.None);
        }
        await using McpProtectedCredentialStore reopened = new(root.Generation);
        Assert.Equal(replacement, await reopened.ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(root.CredentialPath)!));
    }

    [Fact]
    public async Task CopiedCredential_CannotBeReadUnderAnotherGeneration()
    {
        using Root original = new();
        using Root other = new();
        await using McpProtectedCredentialStore first = new(original.Generation);
        await first.WriteAsync(McpAccessCredential.CreateSecret(), CancellationToken.None);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(other.CredentialPath)!);
        File.Copy(original.CredentialPath, other.CredentialPath);
        await using McpProtectedCredentialStore second = new(other.Generation);
        await Assert.ThrowsAsync<InvalidDataException>(() => second.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptCredential_IsNotReportedAsAbsentOrSilentlyReplaced()
    {
        using Root root = new();
        await using McpProtectedCredentialStore store = new(root.Generation);
        await store.WriteAsync(McpAccessCredential.CreateSecret(), CancellationToken.None);
        byte[] bytes = await File.ReadAllBytesAsync(root.CredentialPath);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(root.CredentialPath, bytes);
        await Assert.ThrowsAsync<CryptographicException>(() => store.ReadAsync(CancellationToken.None));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(root.CredentialPath));
    }

    [Fact]
    public async Task InvalidSourceOrDirectoryDestination_DoesNotOverwriteExistingData()
    {
        using Root root = new();
        await using McpProtectedCredentialStore store = new(root.Generation);
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("invalid", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
        Directory.CreateDirectory(root.CredentialPath);
        string sentinel = System.IO.Path.Combine(root.CredentialPath, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(McpAccessCredential.CreateSecret(), CancellationToken.None));
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
    }

    private sealed class Root : IDisposable
    {
        private readonly string _parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        public string Path { get; }
        public DataGenerationDescriptor Generation { get; }
        public string CredentialPath => System.IO.Path.Combine(Path, "Agent", McpProtectedCredentialStore.FileName);
        public Root()
        {
            Path = System.IO.Path.Combine(_parent, "clashsharp-mcp-credential-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Generation = new(Guid.NewGuid(), 1, Path);
        }
        public void Dispose()
        {
            string absolute = System.IO.Path.GetFullPath(Path);
            if (!StringComparer.OrdinalIgnoreCase.Equals(System.IO.Path.GetDirectoryName(absolute), _parent)
                || !System.IO.Path.GetFileName(absolute).StartsWith("clashsharp-mcp-credential-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Credential test cleanup escaped its owned directory.");
            }
            Directory.Delete(absolute, recursive: true);
        }
    }
}

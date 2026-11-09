using System.Reflection;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Contracts of the private single-leaf state files, enumerated over every factory by reflection: each
/// store is bound to its own distinct leaf and diagnostic prefix, rejects every other path spelling
/// before any file-system access, and never trusts, replaces or deletes a file whose ACL it does not own.
/// </summary>
public sealed class WindowsInstallerPrivateJournalFileNativeContractTests : IDisposable
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private readonly string _root;

    public WindowsInstallerPrivateJournalFileNativeContractTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashSharp.Installer.Windows.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // The stores accept only paths that are already canonical; expand any 8.3 alias of the profile.
        _root = Path.GetFullPath(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void EveryFactoryBindsAUniquePlainLeafAndAUniqueValidDiagnosticPrefix()
    {
        StoreBinding[] stores = Stores();
        Assert.True(stores.Length >= 7, "The factory universe must include every private store.");

        Assert.Equal(stores.Length, stores.Select(static store => store.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(stores.Length, stores.Select(static store => store.Prefix).Distinct(StringComparer.Ordinal).Count());
        foreach (StoreBinding store in stores)
        {
            Assert.Equal(store.FileName, Path.GetFileName(store.FileName));
            Assert.False(Path.IsPathRooted(store.FileName));
            Assert.True(store.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, store.FileName);
            Assert.True(store.MaximumBytes is > 0 and <= 1024 * 1024, store.Prefix);
            Assert.Equal(store.FileName, store.FileName.Trim());
            Assert.False(store.FileName.EndsWith('.'), store.FileName);
            foreach (string suffix in new[] { "journal_path_invalid", "document_size_invalid", "file_open_failed", "file_links_invalid", "atomic_replace_uncertain" })
            {
                InstallerProtocolValidation.ValidateDiagnosticCode(store.Prefix + "." + suffix);
            }
        }
    }

    [Fact]
    public async Task EveryStoreRejectsEveryOtherStoresLeafAndEveryNonCanonicalSpellingBeforeTouchingTheDisk()
    {
        StoreBinding[] stores = Stores();
        string missingRoot = @"C:\ClashSharpNoSuchRoot\" + Guid.NewGuid().ToString("N");
        foreach (StoreBinding store in stores)
        {
            var paths = new List<string>();
            paths.AddRange(stores.Where(other => other.FileName != store.FileName).Select(other => missingRoot + @"\" + other.FileName));
            paths.AddRange(
            [
                string.Empty, " ", store.FileName, @"relative\" + store.FileName, missingRoot,
                missingRoot + @"\" + store.FileName.ToUpperInvariant(),
                missingRoot + @"\" + store.FileName + " ",
                missingRoot + @"\" + store.FileName + ".",
                missingRoot + @"\" + store.FileName + ":stream",
                missingRoot + @"\sub\..\" + store.FileName,
                missingRoot + @"\.\" + store.FileName,
                missingRoot + @"\\" + store.FileName,
                missingRoot.Replace('\\', '/') + "/" + store.FileName,
                missingRoot + @"\" + store.FileName + @"\",
                missingRoot + @"\x" + store.FileName,
            ]);
            foreach (string path in paths)
            {
                foreach (Func<Task> operation in new Func<Task>[]
                {
                    () => store.Native.ReadAsync(path, CancellationToken.None),
                    () => store.Native.DeleteAsync(path, CancellationToken.None),
                    () => store.Native.WriteAtomicallyAsync(path, new byte[] { 1 }, CancellationToken.None),
                    () => Task.Run(() => store.Native.IsPresent(path, CancellationToken.None)),
                })
                {
                    Exception? failure = await Record.ExceptionAsync(operation);

                    Assert.True(
                        failure is InstallerProtocolException { DiagnosticCode: var code } && code == store.Prefix + ".journal_path_invalid",
                        $"{store.Prefix} accepted '{path}': {failure?.GetType().Name ?? "no failure"} {(failure as InstallerProtocolException)?.DiagnosticCode}");
                }
            }
        }

        Assert.False(Directory.Exists(missingRoot));
    }

    [Fact]
    public async Task DocumentSizeBoundsAreEnforcedForTheStoresOwnLimitBeforeAnyIo()
    {
        foreach (StoreBinding store in Stores())
        {
            string path = @"C:\ClashSharpNoSuchRoot\" + Guid.NewGuid().ToString("N") + @"\" + store.FileName;

            foreach (int length in new[] { 0, store.MaximumBytes + 1, store.MaximumBytes + 4096 })
            {
                InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
                    () => store.Native.WriteAtomicallyAsync(path, new byte[length], CancellationToken.None));

                Assert.Equal(store.Prefix + ".document_size_invalid", failure.DiagnosticCode);
            }
        }
    }

    [Fact]
    public async Task AMissingLeafInAnExistingDirectoryIsAbsentNotAnError()
    {
        foreach (StoreBinding store in Stores())
        {
            string path = Path.Combine(_root, store.FileName);

            Assert.Null(await store.Native.ReadAsync(path, CancellationToken.None));
            Assert.False(store.Native.IsPresent(path, CancellationToken.None));
            await store.Native.DeleteAsync(path, CancellationToken.None);
            Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        }
    }

    [Fact]
    public async Task AFileWithTheStoresNameButAnotherOwnersAclIsNeverTrustedReplacedOrDeleted()
    {
        foreach (StoreBinding store in Stores())
        {
            string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, store.FileName);
            byte[] foreign = [(byte)'{', (byte)'}'];
            await File.WriteAllBytesAsync(path, foreign);
            string[] before = Directory.EnumerateFileSystemEntries(directory).ToArray();

            foreach (Func<Task> operation in new Func<Task>[]
            {
                () => store.Native.ReadAsync(path, CancellationToken.None),
                () => store.Native.DeleteAsync(path, CancellationToken.None),
                () => store.Native.WriteAtomicallyAsync(path, new byte[] { 1, 2, 3 }, CancellationToken.None),
                () => Task.Run(() => store.Native.IsPresent(path, CancellationToken.None)),
            })
            {
                Exception? failure = await Record.ExceptionAsync(operation);

                Assert.True(
                    failure is InstallerProtocolException,
                    $"{store.Prefix} treated a user-owned file as trusted state: {failure?.GetType().Name ?? "no failure"}.");
                Assert.Equal(foreign, await File.ReadAllBytesAsync(path));
                Assert.Equal(before, Directory.EnumerateFileSystemEntries(directory).ToArray());
            }
        }
    }

    [Fact]
    public async Task CancellationIsObservedBeforeAnyPathProbeForEveryStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (StoreBinding store in Stores())
        {
            string path = Path.Combine(_root, store.FileName);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Native.ReadAsync(path, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Native.DeleteAsync(path, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.Native.WriteAtomicallyAsync(path, new byte[] { 1 }, cancellation.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => store.Native.IsPresent(path, cancellation.Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        }
    }

    private static StoreBinding[] Stores()
    {
        Type type = typeof(WindowsInstallerPrivateJournalFileNative);
        var natives = new List<WindowsInstallerPrivateJournalFileNative>
        {
            (WindowsInstallerPrivateJournalFileNative)type
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
                .GetValue(null)!,
        };
        foreach (MethodInfo factory in type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(static method => method.Name.StartsWith("CreateFor", StringComparison.Ordinal)))
        {
            object?[] arguments = factory.GetParameters()
                .Select(static parameter => parameter.ParameterType == typeof(string)
                    ? (object?)TargetSid
                    : throw new NotSupportedException("Unknown private-journal factory parameter " + parameter.Name))
                .ToArray();
            natives.Add((WindowsInstallerPrivateJournalFileNative)factory.Invoke(null, arguments)!);
        }

        return natives.Select(static native => new StoreBinding(
            native,
            (string)Field(native, "_fileName"),
            (string)Field(native, "_diagnosticPrefix"),
            (int)Field(native, "_maximumDocumentBytes"))).ToArray();
    }

    private static object Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private sealed record StoreBinding(WindowsInstallerPrivateJournalFileNative Native, string FileName, string Prefix, int MaximumBytes);
}

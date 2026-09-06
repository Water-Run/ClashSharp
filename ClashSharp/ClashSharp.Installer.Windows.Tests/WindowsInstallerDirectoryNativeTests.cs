using System.ComponentModel;
using System.Security.Cryptography;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Transactions;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerDirectoryNativeTests
{
    [Fact]
    public void NestedObserversAndMutationGuardPinTheDirectoryUntilEveryLeaseIsReleased()
    {
        using var scope = new TemporaryDirectory();
        var native = new WindowsInstallerDirectoryNative();
        using IWindowsInstallerDirectoryLease outer = native.OpenDirectory(scope.Source);
        using IWindowsInstallerDirectoryLease inner = native.OpenDirectory(scope.Source);
        using SafeFileHandle mutation = WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(scope.Source);

        Assert.True(outer.Observe().IsDirectory);
        Assert.True(inner.Observe().IsDirectory);
        Assert.ThrowsAny<IOException>(() => Directory.Move(scope.Source, scope.Renamed));
        outer.Dispose();
        Assert.ThrowsAny<IOException>(() => Directory.Move(scope.Source, scope.Renamed));
        inner.Dispose();
        Assert.ThrowsAny<IOException>(() => Directory.Move(scope.Source, scope.Renamed));
        mutation.Dispose();

        Directory.Move(scope.Source, scope.Renamed);
        Assert.True(Directory.Exists(scope.Renamed));
    }

    [Fact]
    public void ExistingDeleteAuthorityPreventsAcquiringAnObservationLease()
    {
        using var scope = new TemporaryDirectory();
        var native = new WindowsInstallerDirectoryNative();
        using (SafeFileHandle deletion = WindowsFileSystemNative.OpenOrdinaryDirectoryForDeletion(scope.Source))
        {
            Win32Exception exception = Assert.Throws<Win32Exception>(() => native.OpenDirectory(scope.Source));
            Assert.Equal(32, exception.NativeErrorCode);
        }

        using IWindowsInstallerDirectoryLease lease = native.OpenDirectory(scope.Source);
        Assert.True(lease.Observe().IsDirectory);
        Assert.ThrowsAny<IOException>(() => Directory.Move(scope.Source, scope.Renamed));
    }

    [Fact]
    public void AssociationCanBeCreatedReplacedReadAndDeletedUnderAnObservationLease()
    {
        using var scope = new TemporaryDirectory();
        var native = new WindowsInstallerDirectoryNative();
        WindowsMachineAssociationFileNative files = WindowsMachineAssociationFileNative.Instance;
        string path = Path.Combine(scope.Source, "association.json");
        using (IWindowsInstallerDirectoryLease lease = native.OpenDirectory(scope.Source))
        {
            foreach (char tokenCharacter in new[] { 'a', 'b' })
            {
                InstallerMachineAssociation expected = InstallerMachineAssociation.Create(
                    "S-1-5-21-100-200-300-1001", new string(tokenCharacter, 64));
                byte[] bytes = InstallerMachineAssociationCodec.Serialize(expected);
                try
                {
                    files.WriteAtomically(path, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                WindowsMachineAssociationFileObservation observed = files.Read(path);
                try
                {
                    Assert.Equal(WindowsMachineAssociationFileStatus.OrdinaryFile, observed.Status);
                    Assert.NotNull(observed.Bytes);
                    Assert.Equal(expected, InstallerMachineAssociationCodec.Parse(observed.Bytes));
                }
                finally
                {
                    if (observed.Bytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(observed.Bytes);
                    }
                }
                Assert.False(File.Exists(path + ".new"));
                Assert.ThrowsAny<IOException>(() => Directory.Move(scope.Source, scope.Renamed));
            }

            files.Delete(path);
            Assert.Equal(WindowsMachineAssociationFileStatus.Missing, files.Read(path).Status);
            Assert.ThrowsAny<IOException>(() => Directory.Move(scope.Source, scope.Renamed));
        }

        Directory.Move(scope.Source, scope.Renamed);
        Assert.True(Directory.Exists(scope.Renamed));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _root;

        internal TemporaryDirectory()
        {
            WindowsPayloadFixture.AssertWindows11X64();
            _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"ClashSharp-directory-sharing-{Guid.NewGuid():N}"));
            Source = Path.GetFullPath(Path.Combine(_root, "source"));
            Renamed = Path.GetFullPath(Path.Combine(_root, "renamed"));
            RequireContained(Source);
            RequireContained(Renamed);
            Directory.CreateDirectory(Source);
        }

        internal string Source { get; }

        internal string Renamed { get; }

        public void Dispose()
        {
            foreach (string path in new[] { Source, Renamed })
            {
                RequireContained(path);
                if (Directory.Exists(path))
                {
                    Assert.False((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
                    File.Delete(Path.Combine(path, "association.json"));
                    File.Delete(Path.Combine(path, "association.json.new"));
                    Directory.Delete(path);
                }
            }
            Directory.Delete(_root);
        }

        private void RequireContained(string path) =>
            Assert.StartsWith(_root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }
}

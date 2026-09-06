using System.ComponentModel;
using System.Security.AccessControl;
using ClashSharp.MihomoService;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Tests.Unit.MihomoService;

/// <summary>Verifies that service preparation preserves the Installer-owned directory contract.</summary>
public sealed class MihomoServiceSharedDirectoryGuardTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";
    private const string Product = @"C:\ProgramData\ClashSharp";
    private const string Service = Product + @"\MihomoService";

    /// <summary>Verifies pure construction, exact shared access and owned handle lifetime.</summary>
    [Fact]
    public void AcceptedSharedRootsRemainPinnedUntilTheCallerFinishesPrivatePreparation()
    {
        var native = new FakeDirectories();
        var guard = new MihomoServiceSharedDirectoryGuard(@"C:\ProgramData", TargetSid, native.Open);
        Assert.Empty(native.Opened);

        using IDisposable lifetime = guard.Acquire();

        Assert.Equal([@"C:\", @"C:\ProgramData", Product, Service], native.Opened);
        Assert.Equal(4, native.LiveLeases);
        Assert.Equal(8, native.Observations);
        lifetime.Dispose();
        Assert.Equal(0, native.LiveLeases);
        Assert.Equal([Service, Product, @"C:\ProgramData", @"C:\"], native.Disposed);
        lifetime.Dispose();
        Assert.Equal(4, native.Disposed.Count);
    }

    /// <summary>Verifies either shared root rejects foreign, broadened or malformed evidence.</summary>
    [Theory]
    [InlineData(false, "foreign")]
    [InlineData(true, "foreign")]
    [InlineData(false, "private-system")]
    [InlineData(true, "private-system")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "reparse")]
    [InlineData(true, "reparse")]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [InlineData(false, "sharing")]
    [InlineData(true, "sharing")]
    [InlineData(true, "unprotected")]
    [InlineData(true, "missing-dacl")]
    [InlineData(true, "broad")]
    [InlineData(true, "deny")]
    [InlineData(true, "inherited")]
    [InlineData(true, "object-specific")]
    public void UnsafeSharedRootsFailAndReleaseEveryAcquiredHandle(bool serviceRoot, string condition)
    {
        var native = new FakeDirectories();
        string affected = serviceRoot ? Service : Product;
        WindowsDirectoryObservation original = native.Values[affected];
        WindowsDirectorySecuritySnapshot security = original.Security;
        switch (condition)
        {
            case "missing":
                native.Values.Remove(affected);
                break;
            case "sharing":
                native.SharingFailure = affected;
                break;
            case "foreign":
                native.Values[affected] = Protected(OtherSid);
                break;
            case "private-system":
                native.Values[affected] = original with
                {
                    Security = security with
                    {
                        OwnerSid = WindowsDirectoryAccessPolicy.LocalSystemSid,
                        AccessEntries = security.AccessEntries.Take(2).ToArray(),
                    },
                };
                break;
            case "reparse":
                native.Values[affected] = original with { IsReparsePoint = true };
                break;
            case "file":
                native.Values[affected] = original with { IsDirectory = false };
                break;
            case "unprotected":
                native.Values[affected] = original with { Security = security with { DaclProtected = false } };
                break;
            case "missing-dacl":
                native.Values[affected] = original with { Security = security with { HasDacl = false } };
                break;
            default:
                WindowsDirectoryAce[] entries = security.AccessEntries.ToArray();
                entries[2] = condition switch
                {
                    "broad" => entries[2] with { AccessMask = (int)FileSystemRights.FullControl },
                    "deny" => entries[2] with { Kind = WindowsDirectoryAceKind.Deny },
                    "inherited" => entries[2] with { Flags = entries[2].Flags | AceFlags.Inherited },
                    "object-specific" => entries[2] with { IsObjectSpecific = true },
                    _ => throw new InvalidOperationException(),
                };
                native.Values[affected] = original with { Security = security with { AccessEntries = entries } };
                break;
        }
        var guard = new MihomoServiceSharedDirectoryGuard(@"C:\ProgramData", TargetSid, native.Open);
        WindowsDirectoryObservation? expected = native.Values.GetValueOrDefault(affected);

        MihomoServiceConfigurationTrustException failure = Assert.Throws<MihomoServiceConfigurationTrustException>(guard.Acquire);

        Assert.Equal(0, native.LiveLeases);
        Assert.Equal(expected, native.Values.GetValueOrDefault(affected));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(affected, failure.ToString(), StringComparison.Ordinal);
        if (!serviceRoot)
        {
            Assert.DoesNotContain(Service, native.Opened);
        }
    }

    /// <summary>Verifies a parent ACL change is detected after acquiring the complete chain.</summary>
    [Fact]
    public void RevalidationRejectsChangedSharedRootsBeforeReturningAuthority()
    {
        var native = new FakeDirectories();
        native.AfterObservation = count =>
        {
            if (count == 4)
            {
                native.Values[Product] = Protected(OtherSid);
            }
        };
        var guard = new MihomoServiceSharedDirectoryGuard(@"C:\ProgramData", TargetSid, native.Open);

        Assert.Throws<MihomoServiceConfigurationTrustException>(guard.Acquire);

        Assert.Equal(0, native.LiveLeases);
        Assert.Equal(4, native.Disposed.Count);
    }

    /// <summary>Verifies a user-controlled ancestor is rejected before opening shared children.</summary>
    [Fact]
    public void UnsafeKnownFolderAnchorCannotAuthorizeSharedRootPreparation()
    {
        var native = new FakeDirectories();
        WindowsDirectoryObservation anchor = native.Values[@"C:\ProgramData"];
        native.Values[@"C:\ProgramData"] = anchor with
        {
            Security = anchor.Security with { OwnerSid = TargetSid },
        };
        var guard = new MihomoServiceSharedDirectoryGuard(@"C:\ProgramData", TargetSid, native.Open);

        Assert.Throws<MihomoServiceConfigurationTrustException>(guard.Acquire);

        Assert.Equal([@"C:\", @"C:\ProgramData"], native.Opened);
        Assert.Equal(0, native.LiveLeases);
    }

    private static WindowsDirectoryObservation Protected(string sid) => new(
        true, false,
        new(WindowsDirectoryAccessPolicy.AdministratorsSid, true, true,
        [
            Entry(WindowsDirectoryAccessPolicy.LocalSystemSid, FileSystemRights.FullControl),
            Entry(WindowsDirectoryAccessPolicy.AdministratorsSid, FileSystemRights.FullControl),
            Entry(sid, WindowsDirectoryAccessPolicy.OwnerReadOnlyRights),
        ]));

    private static WindowsDirectoryAce Entry(string sid, FileSystemRights rights) => new(
        sid, WindowsDirectoryAceKind.Allow, (int)rights,
        AceFlags.ContainerInherit | AceFlags.ObjectInherit, false);

    private sealed class FakeDirectories
    {
        internal Dictionary<string, WindowsDirectoryObservation> Values { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\"] = Protected(TargetSid),
            [@"C:\ProgramData"] = Protected(TargetSid),
            [Product] = Protected(TargetSid),
            [Service] = Protected(TargetSid),
        };
        internal List<string> Opened { get; } = [];
        internal List<string> Disposed { get; } = [];
        internal int LiveLeases { get; private set; }
        internal int Observations { get; private set; }
        internal string? SharingFailure { get; set; }
        internal Action<int>? AfterObservation { get; set; }

        internal IWindowsDirectoryReadLease Open(string path)
        {
            Opened.Add(path);
            if (path == SharingFailure)
            {
                throw new Win32Exception(32);
            }
            if (!Values.ContainsKey(path))
            {
                throw new DirectoryNotFoundException();
            }
            LiveLeases++;
            return new Lease(this, path);
        }

        private sealed class Lease(FakeDirectories owner, string path) : IWindowsDirectoryReadLease
        {
            private bool _disposed;

            public WindowsDirectoryObservation Observe()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                WindowsDirectoryObservation observation = owner.Values[path];
                owner.Observations++;
                owner.AfterObservation?.Invoke(owner.Observations);
                return observation;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    owner.Disposed.Add(path);
                    owner.LiveLeases--;
                }
            }
        }
    }
}

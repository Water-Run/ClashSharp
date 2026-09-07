using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferAccessTreeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransfersEverySharedMemberAndPreservesOpaqueProtectedBoundaries(bool propagation)
    {
        var native = new WindowsOwnerTransferAccessFixture { Propagate = propagation };
        WindowsDirectorySecuritySnapshot installer = native.Entries[WindowsOwnerTransferAccessFixture.Installer].Security;
        WindowsDirectorySecuritySnapshot authority = native.Entries[WindowsOwnerTransferAccessFixture.Authority].Security;
        WindowsDirectorySecuritySnapshot service = native.Entries[WindowsOwnerTransferAccessFixture.PrivateService].Security;
        byte[] association = native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes.ToArray();
        using (WindowsOwnerTransferAccessTree tree = native.Acquire())
        {
            Assert.Empty(native.Writes);
            tree.VerifyAssociation(WindowsOwnerTransferAccessFixture.Association);
            tree.ApplyAndVerify(CancellationToken.None);
            tree.VerifyAssociation(WindowsOwnerTransferAccessFixture.Association);
            Assert.Equal(propagation ? 4 : 8, native.Writes.Count);
            Assert.Equal(native.Opened.Count, native.LiveLeases);
            Assert.Equal(installer, native.Entries[WindowsOwnerTransferAccessFixture.Installer].Security);
            Assert.Equal(authority, native.Entries[WindowsOwnerTransferAccessFixture.Authority].Security);
            Assert.Equal(service, native.Entries[WindowsOwnerTransferAccessFixture.PrivateService].Security);
            Assert.Equal(association, native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes);
            Assert.DoesNotContain(WindowsOwnerTransferAccessFixture.Installer, native.Enumerated);
            Assert.DoesNotContain(WindowsOwnerTransferAccessFixture.Authority, native.Enumerated);
            Assert.DoesNotContain(WindowsOwnerTransferAccessFixture.PrivateService, native.Enumerated);
            int writes = native.Writes.Count;
            tree.ApplyAndVerify(CancellationToken.None);
            Assert.Equal(writes, native.Writes.Count);
        }
        Assert.Equal(0, native.LiveLeases);
        Assert.Equal(native.Opened.AsEnumerable().Reverse(), native.Disposed);
    }

    [Fact]
    public void ReplaysMixedOldAndNewAccessWithoutRewritingCompletedNodes()
    {
        var native = new WindowsOwnerTransferAccessFixture();
        WindowsOwnerTransferAccessFixture.Entry completed = native.Entries[WindowsOwnerTransferAccessFixture.Machine];
        completed.Security = WindowsOwnerTransferAccessFixture.OwnerSecurity(
            WindowsOwnerTransferAccessFixture.NextSid, directory: true, inherited: false);
        using WindowsOwnerTransferAccessTree tree = native.Acquire();

        tree.ApplyAndVerify(CancellationToken.None);

        Assert.DoesNotContain(WindowsOwnerTransferAccessFixture.Machine, native.Writes);
        Assert.Contains(WindowsOwnerTransferAccessFixture.Payload, native.Writes);
        tree.Reverify(requireTransferred: true, CancellationToken.None);
    }

    [Theory]
    [InlineData("foreign-owner")]
    [InlineData("foreign-reader")]
    [InlineData("extra-ace")]
    [InlineData("missing-dacl")]
    [InlineData("unprotected-root")]
    [InlineData("protected-payload")]
    [InlineData("inherited-file-flags")]
    [InlineData("reparse")]
    [InlineData("hardlink")]
    [InlineData("private-access-broadened")]
    [InlineData("installer-already-transferred")]
    public void UnsafeTreeIsRejectedBeforeAnyWrite(string failure)
    {
        var native = new WindowsOwnerTransferAccessFixture();
        WindowsOwnerTransferAccessFixture.Entry node = native.Entries[WindowsOwnerTransferAccessFixture.Payload];
        switch (failure)
        {
            case "foreign-owner":
                node.Security = node.Security with { OwnerSid = WindowsOwnerTransferAccessFixture.PreviousSid };
                break;
            case "foreign-reader":
                node.Security = WindowsOwnerTransferAccessFixture.OwnerSecurity("S-1-5-21-100-200-300-1003", false, true);
                break;
            case "extra-ace":
                node.Security = node.Security with { AccessEntries = [.. node.Security.AccessEntries, node.Security.AccessEntries[0]] };
                break;
            case "missing-dacl":
                node.Security = node.Security with { HasDacl = false };
                break;
            case "unprotected-root":
                WindowsOwnerTransferAccessFixture.Entry root = native.Entries[WindowsOwnerTransferAccessFixture.Machine];
                root.Security = root.Security with { DaclProtected = false };
                break;
            case "protected-payload":
                node.Security = WindowsOwnerTransferAccessFixture.OwnerSecurity(WindowsOwnerTransferAccessFixture.PreviousSid, false, false);
                break;
            case "inherited-file-flags":
                node.Security = node.Security with
                {
                    AccessEntries = node.Security.AccessEntries.Select(ace => ace with { Flags = AceFlags.None }).ToArray(),
                };
                break;
            case "reparse":
                node.Reparse = true;
                break;
            case "hardlink":
                node.Links = 2;
                break;
            case "private-access-broadened":
                native.Entries[WindowsOwnerTransferAccessFixture.PrivateService].Security =
                    WindowsOwnerTransferAccessFixture.OwnerSecurity(WindowsOwnerTransferAccessFixture.PreviousSid, true, false);
                break;
            case "installer-already-transferred":
                native.Entries[WindowsOwnerTransferAccessFixture.Installer].Security =
                    WindowsOwnerTransferAccessFixture.OwnerSecurity(WindowsOwnerTransferAccessFixture.NextSid, true, false);
                break;
        }

        Assert.Throws<InstallerProtocolException>(() => native.Acquire());

        Assert.Empty(native.Writes);
        Assert.Equal(0, native.LiveLeases);
    }

    [Theory]
    [InlineData(WindowsOwnerTransferAccessFixture.Authority)]
    [InlineData(WindowsOwnerTransferAccessFixture.Installer)]
    [InlineData(WindowsOwnerTransferAccessFixture.AssociationPath)]
    public void MissingRequiredStateDoesNotCreateOrMigrateAnything(string path)
    {
        var native = new WindowsOwnerTransferAccessFixture();
        native.Entries.Remove(path);

        Assert.Throws<InstallerProtocolException>(() => native.Acquire());

        Assert.Empty(native.Writes);
        Assert.Equal(0, native.LiveLeases);
        Assert.False(native.Entries.ContainsKey(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingFixedContinuationBoundaryReleasesEveryPreviouslyPinnedObject(bool file)
    {
        var native = new WindowsOwnerTransferAccessFixture();
        native.Entries.Remove(file ? WindowsOwnerTransferAccessFixture.ContinuationPath : WindowsOwnerTransferAccessFixture.ContinuationDirectory);

        Assert.Throws<FileNotFoundException>(() => native.Acquire());

        Assert.Empty(native.Writes);
        Assert.Equal(0, native.LiveLeases);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\ClashSharp\unexpected.txt", false)]
    [InlineData(@"C:\ProgramData\ClashSharp\MihomoService\unknown", true)]
    [InlineData(@"C:\Program Files\ClashSharp\Service\unexpected", true)]
    [InlineData(@"C:\Program Files\ClashSharp\Service\current\name.", false)]
    [InlineData(@"C:\Program Files\ClashSharp\Service\current\NUL.txt", false)]
    [InlineData(@"C:\Program Files\ClashSharp\Service\current\COM1.exe", false)]
    [InlineData(@"C:\Program Files\ClashSharp\Service\current\COM¹.exe", false)]
    [InlineData(@"C:\Program Files\ClashSharp\Service\current\CONIN$", false)]
    [InlineData(@"C:\Program Files\ClashSharp\Service\current\NUL .txt", false)]
    public void UnexpectedMembersAndAmbiguousNamesDoNotReceiveNewUserAccess(string path, bool directory)
    {
        var native = new WindowsOwnerTransferAccessFixture();
        native.Add(path, directory, inherited: true);

        Assert.Throws<InstallerProtocolException>(() => native.Acquire());

        Assert.Empty(native.Writes);
        Assert.Equal(0, native.LiveLeases);
    }

    [Fact]
    public void DirectoryMembershipIsRecheckedBeforeTheFirstWrite()
    {
        var native = new WindowsOwnerTransferAccessFixture();
        using WindowsOwnerTransferAccessTree tree = native.Acquire();
        native.Add(WindowsOwnerTransferAccessFixture.Machine + @"\current\late.txt", directory: false, inherited: true);

        Assert.Throws<InstallerProtocolException>(() => tree.ApplyAndVerify(CancellationToken.None));

        Assert.Empty(native.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedTraversalRejectsOversizedOrDeepTreesBeforeMutation(bool deep)
    {
        var native = new WindowsOwnerTransferAccessFixture();
        string parent = WindowsOwnerTransferAccessFixture.Machine + @"\current";
        for (int index = 0; index < (deep ? 15 : 260); index++)
        {
            string path = Path.Combine(parent, $"entry-{index}");
            native.Add(path, directory: deep, inherited: true);
            if (deep)
            {
                parent = path;
            }
        }

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() => native.Acquire());

        Assert.Equal("installer.owner_transfer.access_tree_limit", failure.DiagnosticCode);
        Assert.Empty(native.Writes);
        Assert.Equal(0, native.LiveLeases);
    }

    [Fact]
    public void FirstObservationFailureReleasesTheJustOpenedLease()
    {
        var native = new WindowsOwnerTransferAccessFixture
        {
            BeforeObserve = _ => throw new IOException("Synthetic observation failure."),
        };

        Assert.Throws<IOException>(() => native.Acquire());

        Assert.Equal(0, native.LiveLeases);
        Assert.Single(native.Opened);
        Assert.Single(native.Disposed);
    }

    [Fact]
    public void CancellationLeavesCompletedAccessForExactReplay()
    {
        var native = new WindowsOwnerTransferAccessFixture();
        using var cancellation = new CancellationTokenSource();
        native.BeforeWrite = _ => cancellation.Cancel();
        using (WindowsOwnerTransferAccessTree tree = native.Acquire())
        {
            Assert.ThrowsAny<OperationCanceledException>(() => tree.ApplyAndVerify(cancellation.Token));
        }
        Assert.Single(native.Writes);
        Assert.Equal(0, native.LiveLeases);
        native.BeforeWrite = null;
        string completed = native.Writes[0];
        using WindowsOwnerTransferAccessTree resumed = native.Acquire();
        resumed.ApplyAndVerify(CancellationToken.None);
        Assert.Equal(1, native.Writes.Count(path => path == completed));
    }

    [Fact]
    public void LaterNativeFailurePreservesCompletedChangesAndReleasesTheWholeTree()
    {
        var native = new WindowsOwnerTransferAccessFixture();
        native.BeforeWrite = _ =>
        {
            if (native.Writes.Count == 2)
            {
                throw new IOException("Synthetic mutation failure.");
            }
        };
        using (WindowsOwnerTransferAccessTree tree = native.Acquire())
        {
            Assert.Throws<IOException>(() => tree.ApplyAndVerify(CancellationToken.None));
        }
        Assert.Equal(2, native.Writes.Count);
        Assert.Equal(0, native.LiveLeases);
        native.BeforeWrite = null;
        using WindowsOwnerTransferAccessTree resumed = native.Acquire();
        resumed.ApplyAndVerify(CancellationToken.None);
        Assert.Equal(8, native.Writes.Count);
    }
}

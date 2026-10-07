using System.Security.AccessControl;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsPhysicalVolumeRootTests
{
    [Theory]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\", true)]
    [InlineData(@"\\?\volume{11111111-2222-3333-4444-555555555555}\", true)]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\ordinary", false)]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\ordinary\..\", false)]
    [InlineData(@"C:\", false)]
    [InlineData(@"\\?\C:\", false)]
    [InlineData(@"\Device\HarddiskVolume1\", false)]
    [InlineData(@"\\?\Volume{ZZZZZZZZ-2222-3333-4444-555555555555}\", false)]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}", false)]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\\", false)]
    [InlineData("", false)]
    public void OnlyExactFinalGuidRootsProvePhysicalVolumeIdentity(string finalPath, bool accepted) =>
        Assert.Equal(accepted, WindowsDirectoryReadLease.IsPhysicalVolumeRootGuidPath(finalPath));

    [Fact]
    public void NativePinnedHandlesDistinguishPhysicalRootFromAnOrdinaryDirectory()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string root = Path.GetPathRoot(system)!;
        using WindowsDirectoryReadLease rootLease = WindowsDirectoryReadLease.Open(root);
        using WindowsDirectoryReadLease directoryLease = WindowsDirectoryReadLease.Open(system);

        WindowsDirectoryObservation rootObservation = rootLease.Observe();
        WindowsDirectoryObservation ordinaryObservation = directoryLease.Observe();

        Assert.True(rootObservation.IsDirectory);
        Assert.False(rootObservation.IsReparsePoint);
        Assert.True(rootObservation.IsPhysicalVolumeRoot);
        Assert.True(ordinaryObservation.IsDirectory);
        Assert.False(ordinaryObservation.IsPhysicalVolumeRoot);
        Assert.True(rootLease.Observe().IsPhysicalVolumeRoot);
    }

    [Theory]
    [InlineData("S-1-5-21-100-200-300-1001", true, (int)WindowsDirectoryAceKind.Allow)]
    [InlineData(null, true, (int)WindowsDirectoryAceKind.Allow)]
    [InlineData("S-1-5-18", false, (int)WindowsDirectoryAceKind.Allow)]
    [InlineData("S-1-5-18", true, (int)WindowsDirectoryAceKind.Unsupported)]
    public void RootProofDoesNotAcceptUntrustedOwnerMissingDaclOrUnsupportedAce(string? owner, bool dacl, int kind)
    {
        var security = new WindowsDirectorySecuritySnapshot(owner, dacl, false,
            [new("S-1-5-11", (WindowsDirectoryAceKind)kind, (int)FileSystemRights.Modify, AceFlags.None, false)]);

        Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(security, isPhysicalVolumeRoot: true));
    }

    [Fact]
    public void OwnedDirectoryPolicyAlwaysRejectsTheSameEffectiveDeleteGrant()
    {
        var security = new WindowsDirectorySecuritySnapshot(WindowsDirectoryAccessPolicy.LocalSystemSid, true, false,
            [new("S-1-5-11", WindowsDirectoryAceKind.Allow, (int)FileSystemRights.Modify, AceFlags.None, false)]);

        Assert.True(WindowsDirectoryAccessPolicy.IsTrustedAncestor(security, isPhysicalVolumeRoot: true));
        Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(security, isPhysicalVolumeRoot: false));
        Assert.False(WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(security));
    }
}

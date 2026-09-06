using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerAuthorityMutexSecurityTests
{
    [Fact]
    public void ExactAdministratorAndSystemDescriptorIsAccepted()
    {
        RawSecurityDescriptor security = WindowsInstallerAuthorityMutex.CreateSecurity();

        WindowsInstallerAuthorityMutex.ValidateSecurity(security);

        Assert.Equal(2, security.DiscretionaryAcl!.Count);
        Assert.Equal("S-1-5-32-544", security.Owner!.Value);
    }

    [Theory]
    [InlineData("O:BAG:BAD:P(A;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)(A;;0x1f0001;;;WD)")]
    [InlineData("O:WDG:BAD:P(A;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)")]
    [InlineData("O:BAG:BAD:(A;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)")]
    [InlineData("O:BAG:BAD:P(A;;0x100001;;;SY)(A;;0x1f0001;;;BA)")]
    [InlineData("O:BAG:BAD:P(A;;0x1f0001;;;SY)(A;;0x1f0001;;;SY)")]
    [InlineData("O:BAG:BAD:P(D;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)")]
    [InlineData("O:BAG:BAD:P(A;CI;0x1f0001;;;SY)(A;;0x1f0001;;;BA)")]
    public void ExistingObjectWithUnexpectedSecurityIsRejected(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsInstallerAuthorityMutex.ValidateSecurity(descriptor));

        Assert.Equal("installer.authority.lock_acl_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void CallbackAceCannotGrantAuthority()
    {
        RawSecurityDescriptor descriptor = WindowsInstallerAuthorityMutex.CreateSecurity();
        descriptor.DiscretionaryAcl![0] = new CommonAce(
            AceFlags.None, AceQualifier.AccessAllowed, 0x001F_0001,
            new SecurityIdentifier("S-1-5-18"), isCallback: true, opaque: []);

        Assert.Throws<InstallerProtocolException>(() =>
            WindowsInstallerAuthorityMutex.ValidateSecurity(descriptor));
    }
}

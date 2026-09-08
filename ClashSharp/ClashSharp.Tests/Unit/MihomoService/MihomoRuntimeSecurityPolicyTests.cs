using System.Security.AccessControl;
using ClashSharp.MihomoService;

namespace ClashSharp.Tests.Unit.MihomoService;

/// <summary>Exercises observed native cache descriptors without modifying host filesystem security.</summary>
public sealed class MihomoRuntimeSecurityPolicyTests
{
    [Theory]
    [InlineData(false, "O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")]
    [InlineData(true, "O:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)")]
    [InlineData(true, "O:BAD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)")]
    [InlineData(true, "O:BAD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)")]
    public void TrustedRuntimeObject_PreservesObservedOwnerAndPermissions(bool descendant, string descriptor)
    {
        DirectorySecurity security = new();
        security.SetSecurityDescriptorSddlForm(descriptor);
        byte[] before = security.GetSecurityDescriptorBinaryForm();

        MihomoRuntimeSecurityPolicy.Validate(security, allowAdministratorOwner: descendant);

        Assert.Equal(before, security.GetSecurityDescriptorBinaryForm());
    }

    [Theory]
    [InlineData(false, "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")]
    [InlineData(false, "O:SYD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)")]
    [InlineData(true, "O:S-1-5-21-1-2-3-1001D:P(A;;FA;;;SY)(A;;FA;;;BA)")]
    [InlineData(true, "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GR;;;WD)")]
    [InlineData(true, "O:BAD:P(A;;GR;;;SY)(A;;FA;;;BA)")]
    [InlineData(true, "O:BAD:P(D;;GW;;;SY)(A;;FA;;;SY)(A;;FA;;;BA)")]
    [InlineData(true, "O:BAD:P(A;OIIO;FA;;;SY)(A;;FA;;;BA)")]
    public void UntrustedRuntimeObject_IsRejectedWithoutRepair(bool descendant, string descriptor)
    {
        DirectorySecurity security = new();
        security.SetSecurityDescriptorSddlForm(descriptor);
        byte[] before = security.GetSecurityDescriptorBinaryForm();

        Assert.Throws<UnauthorizedAccessException>(() =>
            MihomoRuntimeSecurityPolicy.Validate(security, allowAdministratorOwner: descendant));

        Assert.Equal(before, security.GetSecurityDescriptorBinaryForm());
    }
}

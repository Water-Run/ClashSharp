using ClashSharp.ApplicationModel.Security;

namespace ClashSharp.Tests.Unit.Services;

internal sealed class FixedControllerCredentialProvider : IControllerCredentialProvider
{
    public string GetSecret() => "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}

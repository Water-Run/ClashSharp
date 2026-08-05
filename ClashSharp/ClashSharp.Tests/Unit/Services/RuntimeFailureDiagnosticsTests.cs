using ClashSharp.Model;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Locks the stable release-facing runtime diagnostic taxonomy.</summary>
public sealed class RuntimeFailureDiagnosticsTests
{
    public static TheoryData<string, RuntimeFailureArea> AreaCases => new()
    {
        { RuntimeFailureDiagnostics.ServiceUnavailable, RuntimeFailureArea.Service },
        { RuntimeFailureDiagnostics.TunConflict, RuntimeFailureArea.Tun },
        { RuntimeFailureDiagnostics.ConfigurationRejected, RuntimeFailureArea.Configuration },
        { RuntimeFailureDiagnostics.ProviderUpdateFailed, RuntimeFailureArea.Provider },
        { RuntimeFailureDiagnostics.GeoAssetsMissing, RuntimeFailureArea.GeoData },
        { RuntimeFailureDiagnostics.ControllerUnavailable, RuntimeFailureArea.Controller },
        { RuntimeFailureDiagnostics.MixedPortOccupied, RuntimeFailureArea.MixedPort },
        { RuntimeFailureDiagnostics.RouteConflict, RuntimeFailureArea.Route },
        { RuntimeFailureDiagnostics.DnsConflict, RuntimeFailureArea.Dns },
        { "service.child.configuration_untrusted", RuntimeFailureArea.Configuration },
        { "service.controller.provider_update_invalid", RuntimeFailureArea.Provider },
    };

    [Theory]
    [MemberData(nameof(AreaCases))]
    public void Describe_ClassifiesStableSubsystemCode(string code, RuntimeFailureArea expectedArea)
    {
        RuntimeFailureDescriptor descriptor = RuntimeFailureDiagnostics.Describe(code);

        Assert.Equal(code, descriptor.Code);
        Assert.Equal(expectedArea, descriptor.Area);
        Assert.StartsWith("RuntimeFailure.", descriptor.MessageResourceKey, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Service.Unavailable")]
    [InlineData("service unavailable")]
    [InlineData("service")]
    [InlineData(".service")]
    [InlineData("service.")]
    [InlineData("service..unavailable")]
    [InlineData("service.private_secret")]
    [InlineData("private.secret")]
    [InlineData("github.com")]
    public void IsStableCode_RejectsUnboundedOrNonCanonicalText(string? value)
    {
        Assert.False(RuntimeFailureDiagnostics.IsStableCode(value));
    }

    [Fact]
    public void Format_UsesLocalizedActionAndIncludesSupportCode()
    {
        string text = RuntimeFailureDiagnostics.Format(
            RuntimeFailureDiagnostics.GeoAssetsMissing,
            key => key == "RuntimeFailure.GeoData" ? "Run installer repair." : key,
            "fallback");

        Assert.Equal("Run installer repair. [geo.assets_missing]", text);
    }

    [Fact]
    public void Format_AllowsBoundedProvisioningCodeWithoutAllowingPrivateServiceText()
    {
        string text = RuntimeFailureDiagnostics.Format(
            "service.provisioning.association_invalid",
            key => key == "RuntimeFailure.Service" ? "Run installer repair." : key,
            "fallback");

        Assert.Equal(
            "Run installer repair. [service.provisioning.association_invalid]",
            text);
        Assert.False(RuntimeFailureDiagnostics.IsStableCode("service.private_secret"));
    }

    [Fact]
    public void Describe_EndpointIdentityFailure_UsesSpecificAction()
    {
        RuntimeFailureDescriptor descriptor = RuntimeFailureDiagnostics.Describe(
            "service.ipc.endpoint_occupied");

        Assert.Equal(RuntimeFailureArea.Service, descriptor.Area);
        Assert.Equal("RuntimeFailure.EndpointOccupied", descriptor.MessageResourceKey);
    }

    [Fact]
    public void ExtractCode_DoesNotExposeArbitraryExceptionMessage()
    {
        InvalidOperationException exception = new("private path C:\\private\\profile.yaml");

        string code = RuntimeFailureDiagnostics.ExtractCode(
            exception,
            RuntimeFailureDiagnostics.ProviderUpdateFailed);

        Assert.Equal(RuntimeFailureDiagnostics.ProviderUpdateFailed, code);
    }
}

using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Execution;
using Windows.Storage;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerUserSetupTests
{
    [Fact]
    public void ConsumedDefaultsDoNotDiscardTheOwnedUnfinishedNativeStartupWork()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        WindowsInstallerPreferencesDocument document = Document(fixture);
        ApplicationDataCompositeValue request = Request(document);
        Assert.True(WindowsInstallerUserSetup.ShouldRunBootstrap(false, request, document));
        Assert.True(WindowsInstallerUserSetup.ShouldRunBootstrap(true, request, document));
        Assert.False(WindowsInstallerUserSetup.ShouldRunBootstrap(false, null, document));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("startup")]
    [InlineData("recovery")]
    [InlineData("applyDefaults")]
    [InlineData("schema")]
    [InlineData("unknown")]
    public void RetryRejectsChangedOrForeignBootstrapIntent(string key)
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        WindowsInstallerPreferencesDocument document = Document(fixture);
        ApplicationDataCompositeValue request = Request(document);
        request[key] = key switch
        {
            "id" => Guid.NewGuid().ToString("N"),
            "schema" => 2,
            _ => !(request.TryGetValue(key, out object? value) && Equals(value, true)),
        };
        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            WindowsInstallerUserSetup.ShouldRunBootstrap(false, request, document));
        Assert.Equal("installer.preferences.bootstrap_request_conflict", failure.DiagnosticCode);
    }

    private static WindowsInstallerPreferencesDocument Document(WindowsPayloadFixture fixture) => new(1, Guid.NewGuid().ToString("N"),
        fixture.Manifest.PackageIdentity.PackageFamilyName, fixture.Manifest.ExpectedPackageVersion,
        fixture.Manifest.InstallerPayloadSha256, InstallerPreferences.Default, CoreVerified: true);

    private static ApplicationDataCompositeValue Request(WindowsInstallerPreferencesDocument document) => new()
    {
        ["schema"] = 1,
        ["id"] = document.Id,
        ["startup"] = document.Preferences.LaunchAtStartup,
        ["recovery"] = document.Preferences.RegisterProxyRecovery,
        ["applyDefaults"] = true,
    };
}

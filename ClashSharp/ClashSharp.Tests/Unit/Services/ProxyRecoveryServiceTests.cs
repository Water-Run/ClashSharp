using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for startup stale-proxy recovery.</summary>
public sealed class ProxyRecoveryServiceTests
{
    /// <summary>Verifies disabled Windows proxy state is never classified as stale.</summary>
    [Fact]
    public void IsStaleClashProxy_WhenProxyIsDisabled_ReturnsFalse()
    {
        ProxyRecoveryService service = CreateService();

        bool isStale = service.IsStaleClashProxy(new WindowsProxyState(false, "127.0.0.1:19090"), 19090);

        Assert.False(isStale);
    }

    /// <summary>Verifies stale system proxy detection recognizes owned loopback endpoints without mutating them.</summary>
    [Fact]
    public void IsStaleClashProxy_WhenOwnedLoopbackEndpoint_ReturnsTrue()
    {
        ProxyRecoveryService service = CreateService();

        bool isStale = service.IsStaleClashProxy(
            new WindowsProxyState(true, "http=127.0.0.1:19090;https=localhost:19090"),
            19090);

        Assert.True(isStale);
    }

    /// <summary>Verifies loopback and target port must belong to the same proxy endpoint.</summary>
    [Fact]
    public void IsStaleClashProxy_WhenLoopbackAndTargetPortAreOnDifferentEndpoints_ReturnsFalse()
    {
        ProxyRecoveryService service = CreateService();
        WindowsProxyState state = new(true, "http=127.0.0.1:18080;https=corp-proxy:19090");

        bool isStale = service.IsStaleClashProxy(state, 19090);

        Assert.False(isStale);
    }

    private static ProxyRecoveryService CreateService()
    {
        return new ProxyRecoveryService();
    }

    /// <summary>Startup checks must not report an actively owned proxy as leftover state.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IsStaleClashProxy_AfterStartupRestoration_RespectsLiveOwnership(bool runningOwnedProxy, bool expectedStale)
    {
        ProxyRecoveryService service = CreateService();

        bool stale = service.IsStaleClashProxy(
            new WindowsProxyState(true, "127.0.0.1:19090"), 19090, runningOwnedProxy);

        Assert.Equal(expectedStale, stale);
    }

    [Theory]
    [InlineData(false, "", true, true)]
    [InlineData(true, "corporate.example:8080", true, true)]
    [InlineData(true, "127.0.0.1:18080", true, true)]
    [InlineData(false, "", false, false)]
    [InlineData(true, "corporate.example:8080", false, false)]
    [InlineData(true, "127.0.0.1:19090", false, true)]
    public void StartupRecovery_RetiresProvenJournalAfterExternalChangesWithoutClaimingAnUnownedProxy(
        bool enabled, string server, bool journal, bool expectedRecovery)
    {
        var service = CreateService();
        Assert.Equal(expectedRecovery, service.RequiresStartupRecovery(new(enabled, server), 19090, journal));
    }
}

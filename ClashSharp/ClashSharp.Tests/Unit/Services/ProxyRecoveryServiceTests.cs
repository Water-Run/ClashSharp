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
}

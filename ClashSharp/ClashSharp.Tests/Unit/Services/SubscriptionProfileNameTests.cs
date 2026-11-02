using System.Net.Http;
using System.Text;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class SubscriptionProfileNameTests
{
    [Fact]
    public void ExtendedContentDispositionProvidesAUnicodeName()
    {
        using HttpResponseMessage response = new() { Content = new StringContent("rules: []") };
        response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename*=UTF-8''%E8%AE%A2%E9%98%85%E6%9C%8D%E5%8A%A1.yaml");
        Assert.Equal("订阅服务", SubscriptionProfileName.Read(response));
    }

    [Fact]
    public void ExplicitProfileTitleTakesPrecedenceOverFileName()
    {
        using HttpResponseMessage response = new() { Content = new StringContent("rules: []") };
        response.Headers.TryAddWithoutValidation("profile-title", "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Provider plan")));
        response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=other.yaml");
        Assert.Equal("Provider plan", SubscriptionProfileName.Read(response));
    }

    [Fact]
    public void InvalidEncodedTitleCanFallBackWithoutLeakingHeaderData()
    {
        using HttpResponseMessage response = new() { Content = new StringContent("rules: []") };
        response.Headers.TryAddWithoutValidation("profile-title", "base64:not-valid");
        response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=Fallback.yml");
        Assert.Equal("Fallback", SubscriptionProfileName.Read(response));
    }

    [Fact]
    public void MissingMetadataDoesNotTurnASecretUrlIntoAName()
    {
        using HttpResponseMessage response = new() { RequestMessage = new(HttpMethod.Get, "https://example.test/opaque-secret?token=private") };
        Assert.Null(SubscriptionProfileName.Read(response));
    }
}

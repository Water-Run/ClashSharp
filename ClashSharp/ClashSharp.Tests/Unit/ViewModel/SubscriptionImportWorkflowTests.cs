using System.Net.Http;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed class SubscriptionImportWorkflowTests
{
    [Fact]
    public async Task NewUrlIsDownloadedImmediatelyAndCanUseAnOptionalName()
    {
        Catalog catalog = new();
        var result = await SubscriptionImportWorkflow.ImportAsync(catalog, "", " https://example.com/sub.yaml ", CancellationToken.None);
        Assert.Equal("example.com", result.Link.Name);
        Assert.Equal(1, catalog.ImportCount);
        Assert.Equal(result.Link.Id, result.Profile.ProfileId);
        Assert.Single(catalog.Links);
    }

    [Fact]
    public async Task RetryingTheSameUrlReusesItsLinkInsteadOfCreatingDuplicates()
    {
        Catalog catalog = new();
        await SubscriptionImportWorkflow.ImportAsync(catalog, "First", "https://example.com/sub.yaml", CancellationToken.None);
        await SubscriptionImportWorkflow.ImportAsync(catalog, "Second", "https://EXAMPLE.com/sub.yaml", CancellationToken.None);
        Assert.Single(catalog.Links);
        Assert.Equal(2, catalog.ImportCount);
    }

    [Fact]
    public async Task FailedDownloadKeepsTheLinkForRetry()
    {
        Catalog catalog = new() { FailDownload = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => SubscriptionImportWorkflow.ImportAsync(catalog,
            "Example", "https://example.com/sub.yaml", CancellationToken.None));
        Assert.Single(catalog.Links);
        catalog.FailDownload = false;
        await SubscriptionImportWorkflow.ImportAsync(catalog, "Example", "https://example.com/sub.yaml", CancellationToken.None);
        Assert.Single(catalog.Links);
        Assert.Equal(2, catalog.ImportCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("file:///C:/profile.yaml")]
    [InlineData("not a subscription")]
    public async Task InvalidInputDoesNotPersistOrDownload(string input)
    {
        Catalog catalog = new();
        await Assert.ThrowsAsync<ArgumentException>(() => SubscriptionImportWorkflow.ImportAsync(catalog, "Name", input, CancellationToken.None));
        Assert.Empty(catalog.Links);
        Assert.Equal(0, catalog.ImportCount);
    }

    [Fact]
    public async Task CancellationAfterSavingTheLinkDoesNotStartADownload()
    {
        using CancellationTokenSource lifetime = new();
        Catalog catalog = new() { OnAdded = lifetime.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SubscriptionImportWorkflow.ImportAsync(catalog,
            "Example", "https://example.com/sub.yaml", lifetime.Token));
        Assert.Single(catalog.Links);
        Assert.Equal(0, catalog.ImportCount);
    }

    private sealed class Catalog : ISubscriptionLinkCatalog
    {
        public List<ProfileSubscriptionLink> Links { get; } = [];
        public int ImportCount { get; private set; }
        public bool FailDownload { get; set; }
        public Action? OnAdded { get; init; }
        public IReadOnlyList<ProfileSubscriptionLink> GetSubscriptionLinks() => Links;
        public Task<ProfileSubscriptionLink> AddSubscriptionLinkAsync(string name, string uri, CancellationToken cancellationToken)
        {
            ProfileSubscriptionLink link = new() { Id = Guid.NewGuid().ToString("N"), Name = name, Uri = uri, Status = "Saved", IsEnabled = true };
            Links.Add(link);
            OnAdded?.Invoke();
            return Task.FromResult(link);
        }
        public Task<ProfileImportResult> ImportSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken)
        {
            ImportCount++;
            if (FailDownload) { throw new HttpRequestException("download failed"); }
            return Task.FromResult(new ProfileImportResult(link.Id, link.Name, "profile.yaml", 2, 3, "Imported"));
        }
        public Task<bool> TryUpdateSubscriptionLinkAsync(SubscriptionLinkEditRequest request, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> TryDeleteSubscriptionLinkAsync(string linkId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string> CheckSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken) => Task.FromResult("Available");
    }
}

using ClashSharp.Model;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Tests profile-page state derived through explicit dependencies.</summary>
public sealed class ProfilesViewModelTests
{
    /// <summary>Verifies the persisted-id fallback is injected instead of service-located.</summary>
    [Fact]
    public async Task LoadAsync_WhenNoCatalogRowIsActive_UsesInjectedActiveProfileId()
    {
        using TempDirectory tempDirectory = new();
        FakeProfileCatalogSettings settings = new()
        {
            ActiveProfileId = "catalog-id-without-a-row",
        };
        ProfileCatalogService profiles = new(
            Path.Combine(tempDirectory.Path, "profiles.json"),
            Path.Combine(tempDirectory.Path, "mihomo", "history"),
            settings,
            new FakeProfileCatalogCoreConfiguration(),
            new FakeProfileCatalogRuntime(),
            new FakeProfileCatalogLog(),
            static key => key,
            UncoordinatedProfileCatalogMutationCoordinator.Instance);
        LogStorageService logStorage = new(
            Path.Combine(tempDirectory.Path, "logs.db"),
            static () => "unused-log-profile");

        int activeProfileReadCount = 0;
        ProfilesViewModel viewModel = new(
            static key => key,
            new ProfileManagementCatalogAdapter(profiles),
            new PageLogAdapter(logStorage),
            () =>
            {
                activeProfileReadCount++;
                return "injected-active-profile";
            },
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));

        Assert.Empty(viewModel.Profiles);
        Assert.Equal(string.Empty, viewModel.ActiveProfileText);
        Assert.Equal(0, activeProfileReadCount);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("injected-active-profile", viewModel.ActiveProfileText);
        Assert.Equal(1, activeProfileReadCount);
    }

    [Fact]
    public async Task LinksViewModel_LoadAsync_LoadsLinksAfterPureConstruction()
    {
        using TempDirectory tempDirectory = new();
        ProfileCatalogService profiles = CreateProfileCatalog(tempDirectory);
        await profiles.AddSubscriptionLinkAsync(
            "Example",
            "https://example.com/profile.yaml",
            CancellationToken.None);
        LogStorageService logStorage = new(
            Path.Combine(tempDirectory.Path, "links-logs.db"),
            static () => "unused-log-profile");
        LinksViewModel viewModel = new(
            static key => key,
            new SubscriptionLinkCatalogAdapter(profiles),
            new PageLogAdapter(logStorage),
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));

        Assert.Empty(viewModel.SubscriptionLinks);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Single(viewModel.SubscriptionLinks);
        Assert.Equal("Example", viewModel.SubscriptionLinks[0].NameDisplay);
    }

    [Fact]
    public async Task LogsViewModel_LoadAsync_LoadsRowsAfterPureConstruction()
    {
        using TempDirectory tempDirectory = new();
        LogStorageService logStorage = new(
            Path.Combine(tempDirectory.Path, "visible-logs.db"),
            static () => "profile");
        logStorage.AppendLog("Info", "Test", "Visible message", null);
        LogsViewModel viewModel = new(
            static key => key,
            new LogManagementStoreAdapter(logStorage),
            new TestApplicationErrorSink());

        Assert.Empty(viewModel.RecentLogs);
        Assert.Equal(string.Empty, viewModel.StorageUsageText);

        await viewModel.LoadAsync(CancellationToken.None);

        LogRecordDisplay row = Assert.Single(viewModel.RecentLogs);
        Assert.Equal("Visible message", row.Message);
    }

    private static ProfileCatalogService CreateProfileCatalog(
        TempDirectory tempDirectory)
    {
        return new ProfileCatalogService(
            Path.Combine(tempDirectory.Path, "profiles.json"),
            Path.Combine(tempDirectory.Path, "mihomo", "history"),
            new FakeProfileCatalogSettings(),
            new FakeProfileCatalogCoreConfiguration(),
            new FakeProfileCatalogRuntime(),
            new FakeProfileCatalogLog(),
            static key => key,
            UncoordinatedProfileCatalogMutationCoordinator.Instance);
    }

    /// <summary>Verifies accepted and rejected input produce visible results and correct selection state.</summary>
    [Fact]
    public async Task LinksViewModel_AddAndDelete_ReportResultsAndSelectOnlyExistingRows()
    {
        using TempDirectory tempDirectory = new();
        ProfileCatalogService profiles = CreateProfileCatalog(tempDirectory);
        LogStorageService logs = new(Path.Combine(tempDirectory.Path, "links-feedback.db"), static () => "profile");
        LinksViewModel viewModel = new(static key => key, new SubscriptionLinkCatalogAdapter(profiles),
            new PageLogAdapter(logs), new TestApplicationErrorSink(), new ModelDisplayMapper(static text => text));

        await viewModel.AddSubscriptionLinkAsync("Invalid", string.Empty, CancellationToken.None);
        Assert.True(viewModel.HasNoLinks);
        Assert.False(viewModel.HasSelectedLink);
        Assert.True(viewModel.HasStatusText);
        Assert.Equal("Links.Status.AddFailed", viewModel.StatusText);

        await viewModel.AddSubscriptionLinkAsync("Example", "https://example.com/profile.yaml", CancellationToken.None);
        Assert.Same(Assert.Single(viewModel.SubscriptionLinks), viewModel.SelectedLink);
        Assert.Equal("Links.Status.Added", viewModel.StatusText);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Same(Assert.Single(viewModel.SubscriptionLinks), viewModel.SelectedLink);
        await viewModel.DeleteSelectedLinkAsync(CancellationToken.None);
        Assert.True(viewModel.HasNoLinks);
        Assert.False(viewModel.HasSelectedLink);
        Assert.Equal("Links.Status.Deleted", viewModel.StatusText);
    }

    /// <summary>Verifies malformed editor values remain distinguishable from valid input.</summary>
    [Theory]
    [InlineData("", "https://example.com/", 24, "Links.Validation.Name")]
    [InlineData(" ", "https://example.com/", 24, "Links.Validation.Name")]
    [InlineData("Name", "", 24, "Links.Validation.Uri")]
    [InlineData("Name", "not a url", 24, "Links.Validation.Uri")]
    [InlineData("Name", "file:///C:/config.yaml", 24, "Links.Validation.Uri")]
    [InlineData("Name", "ftp://example.com/config", 24, "Links.Validation.Uri")]
    [InlineData("Name", "https://example.com/", 0, "Links.Validation.Interval")]
    [InlineData("Name", "https://example.com/", 8761, "Links.Validation.Interval")]
    [InlineData("Name", "https://example.com/", 1.5, "Links.Validation.Interval")]
    [InlineData("Name", "https://example.com/", double.NaN, "Links.Validation.Interval")]
    [InlineData("Name", "https://example.com/", 1, null)]
    [InlineData("Name", "http://127.0.0.1:18080/subscription.yaml", 8760, null)]
    public void LinksEditor_ValidatesBeforeClosing(string name, string uri, double interval, string? expected)
    {
        Assert.Equal(expected, LinksViewModel.ValidateInput(name, uri, interval));
    }

    private sealed class FakeProfileCatalogSettings : IProfileCatalogSettings
    {
        public string ActiveProfileId { get; set; } = string.Empty;
    }

    private sealed class FakeProfileCatalogCoreConfiguration : IProfileCatalogCoreConfiguration
    {
        public Task<ProfileImportResult> ImportProfileConfigurationAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public CoreConfigurationState EnsureDefaultConfiguration()
        {
            throw new NotSupportedException();
        }

        public Task<string?> ReadImportedProfileConfigurationAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<ProfileImportResult> ValidateImportedProfileAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeProfileCatalogLog : IProfileCatalogLog
    {
        public void AppendLog(string level, string category, string message, string? detail)
        {
        }
    }

    private sealed class FakeProfileCatalogRuntime : IProfileCatalogRuntime
    {
        public Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"clashsharp-profiles-view-model-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

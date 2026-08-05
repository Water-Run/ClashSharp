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

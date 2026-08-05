using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Verifies profile and subscription mutations respect their owning page lifetime.</summary>
public sealed class ProfileAndLinkLifecycleViewModelTests
{
    [Fact]
    public async Task ImportLocalProfileAsync_WhenSuccessful_ReloadsProfilesAsynchronously()
    {
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("before", isActive: true)],
        };
        catalog.ImportLocalProfile = (_, _) =>
        {
            catalog.Profiles = [CreateProfile("after", isActive: true)];
            return Task.FromResult(CreateImportResult("after"));
        };
        RecordingPageLog log = new();
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, log);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ImportLocalProfileAsync("profile.yaml", CancellationToken.None);

        ConfigurationProfileDisplay row = Assert.Single(viewModel.Profiles);
        Assert.Equal("after", row.Id);
        Assert.Equal(2, catalog.GetProfilesCallCount);
        Assert.Contains(
            log.Entries,
            entry => entry.Message.Contains("Local profile imported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportLocalProfileAsync_WhenLifetimeEnds_DoesNotApplyStaleReload()
    {
        TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ProfileImportResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("visible", isActive: true)],
            ImportLocalProfile = (_, _) =>
            {
                started.SetResult();
                return completion.Task;
            },
        };
        RecordingPageLog log = new();
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, log);
        await viewModel.LoadAsync(CancellationToken.None);
        using CancellationTokenSource lifetime = new();

        Task import = viewModel.ImportLocalProfileAsync("profile.yaml", lifetime.Token);
        await started.Task;
        catalog.Profiles = [CreateProfile("stale", isActive: true)];
        lifetime.Cancel();
        completion.SetResult(CreateImportResult("stale"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        Assert.Equal("visible", Assert.Single(viewModel.Profiles).Id);
        Assert.Equal(1, catalog.GetProfilesCallCount);
        Assert.DoesNotContain(
            log.Entries,
            entry => entry.Message.Contains("Local profile imported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetSelectedProfileActiveAsync_WhenSuccessful_AwaitsReload()
    {
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("candidate", isActive: false)],
        };
        catalog.SetActiveProfile = (_, _) =>
        {
            catalog.Profiles = [CreateProfile("candidate", isActive: true)];
            return Task.FromResult(true);
        };
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = Assert.Single(viewModel.Profiles);

        await viewModel.SetSelectedProfileActiveAsync(CancellationToken.None);

        Assert.True(Assert.Single(viewModel.Profiles).IsActive);
        Assert.Equal(2, catalog.GetProfilesCallCount);
    }

    [Fact]
    public async Task SetSelectedProfileActiveAsync_WhenLifetimeEnds_DoesNotApplyStaleReload()
    {
        TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("candidate", isActive: false)],
            SetActiveProfile = (_, _) =>
            {
                started.SetResult();
                return completion.Task;
            },
        };
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = Assert.Single(viewModel.Profiles);
        using CancellationTokenSource lifetime = new();

        Task activation = viewModel.SetSelectedProfileActiveAsync(lifetime.Token);
        await started.Task;
        catalog.Profiles = [CreateProfile("candidate", isActive: true)];
        lifetime.Cancel();
        completion.SetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
        Assert.False(Assert.Single(viewModel.Profiles).IsActive);
        Assert.Equal(1, catalog.GetProfilesCallCount);
    }

    [Fact]
    public async Task AddSubscriptionLinkAsync_WhenSuccessful_AwaitsReload()
    {
        FakeSubscriptionLinkCatalog catalog = new();
        ProfileSubscriptionLink added = CreateLink("added");
        catalog.AddSubscriptionLink = (_, _, _) =>
        {
            catalog.Links = [added];
            return Task.FromResult(added);
        };
        LinksViewModel viewModel = CreateLinksViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.AddSubscriptionLinkAsync(
            added.Name,
            added.Uri,
            CancellationToken.None);

        Assert.Equal("added", Assert.Single(viewModel.SubscriptionLinks).Model.Id);
        Assert.Equal(2, catalog.GetLinksCallCount);
    }

    [Fact]
    public async Task AddSubscriptionLinkAsync_WhenLifetimeEnds_DoesNotApplyStaleReload()
    {
        TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ProfileSubscriptionLink> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSubscriptionLinkCatalog catalog = new()
        {
            Links = [CreateLink("visible")],
            AddSubscriptionLink = (_, _, _) =>
            {
                started.SetResult();
                return completion.Task;
            },
        };
        LinksViewModel viewModel = CreateLinksViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        using CancellationTokenSource lifetime = new();

        Task add = viewModel.AddSubscriptionLinkAsync(
            "Stale",
            "https://example.com/stale.yaml",
            lifetime.Token);
        await started.Task;
        ProfileSubscriptionLink staleLink = CreateLink("stale");
        catalog.Links = [staleLink];
        lifetime.Cancel();
        completion.SetResult(staleLink);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => add);
        Assert.Equal("visible", Assert.Single(viewModel.SubscriptionLinks).Model.Id);
        Assert.Equal(1, catalog.GetLinksCallCount);
    }

    [Fact]
    public async Task AddLinkCommand_WhenPersistenceIsUnauthorized_UsesSafeErrorHandling()
    {
        FakeSubscriptionLinkCatalog catalog = new()
        {
            AddSubscriptionLink = static (_, _, _) =>
                Task.FromException<ProfileSubscriptionLink>(
                    new UnauthorizedAccessException("denied")),
        };
        RecordingPageLog log = new();
        TestApplicationErrorSink errorSink = new();
        LinksViewModel viewModel = CreateLinksViewModel(catalog, log, errorSink);

        await viewModel.AddLinkCommand.ExecuteObservedAsync(
            ("Denied", "https://example.com/denied.yaml"),
            CancellationToken.None);

        Assert.Null(viewModel.AddLinkCommand.LastError);
        Assert.Empty(errorSink.Errors);
        Assert.Contains(
            log.Entries,
            entry => entry.Level == "Warning"
                && entry.Message == "Subscription link could not be added.");
    }

    private static ProfilesViewModel CreateProfilesViewModel(
        IProfileManagementCatalog catalog,
        IPageLog log)
    {
        return new ProfilesViewModel(
            static key => key,
            catalog,
            log,
            static () => "fallback",
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
    }

    private static LinksViewModel CreateLinksViewModel(
        ISubscriptionLinkCatalog catalog,
        IPageLog log,
        IApplicationErrorSink? errorSink = null)
    {
        return new LinksViewModel(
            static key => key,
            catalog,
            log,
            errorSink ?? new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
    }

    private static ConfigurationProfile CreateProfile(string id, bool isActive)
    {
        return new ConfigurationProfile(
            id,
            id,
            "source",
            "available",
            DateTimeOffset.UnixEpoch,
            1,
            1,
            isActive);
    }

    private static ProfileSubscriptionLink CreateLink(string id)
    {
        return new ProfileSubscriptionLink(
            id,
            id,
            $"https://example.com/{id}.yaml",
            true,
            24,
            DateTimeOffset.UnixEpoch,
            "available");
    }

    private static ProfileImportResult CreateImportResult(string id)
    {
        return new ProfileImportResult(
            id,
            id,
            $"{id}.yaml",
            1,
            1,
            "imported");
    }

    private sealed class FakeProfileManagementCatalog : IProfileManagementCatalog
    {
        public IReadOnlyList<ConfigurationProfile> Profiles { get; set; } = [];

        public int GetProfilesCallCount { get; private set; }

        public Func<string, CancellationToken, Task<ProfileImportResult>> ImportLocalProfile { get; set; } =
            static (_, _) => throw new NotSupportedException();

        public Func<string, CancellationToken, Task<bool>> SetActiveProfile { get; set; } =
            static (_, _) => throw new NotSupportedException();

        public IReadOnlyList<ConfigurationProfile> GetProfiles()
        {
            GetProfilesCallCount++;
            return Profiles;
        }

        public IReadOnlyList<ProfileHistoryEntry> GetProfileHistory(string profileId)
        {
            return [];
        }

        public Task<ProfileImportResult> ImportLocalProfileAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            return ImportLocalProfile(filePath, cancellationToken);
        }

        public Task<ProfileImportResult> ValidateProfileAsync(
            ConfigurationProfile profile,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TrySetActiveProfileAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            return SetActiveProfile(profileId, cancellationToken);
        }

        public Task<bool> TryRenameProfileAsync(
            string profileId,
            string name,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryDeleteProfileAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ProfileImportResult> RollbackProfileAsync(
            ProfileHistoryEntry historyEntry,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSubscriptionLinkCatalog : ISubscriptionLinkCatalog
    {
        public IReadOnlyList<ProfileSubscriptionLink> Links { get; set; } = [];

        public int GetLinksCallCount { get; private set; }

        public Func<string, string, CancellationToken, Task<ProfileSubscriptionLink>> AddSubscriptionLink { get; set; } =
            static (_, _, _) => throw new NotSupportedException();

        public IReadOnlyList<ProfileSubscriptionLink> GetSubscriptionLinks()
        {
            GetLinksCallCount++;
            return Links;
        }

        public Task<ProfileSubscriptionLink> AddSubscriptionLinkAsync(
            string name,
            string uri,
            CancellationToken cancellationToken)
        {
            return AddSubscriptionLink(name, uri, cancellationToken);
        }

        public Task<string> CheckSubscriptionLinkAsync(
            ProfileSubscriptionLink link,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryUpdateSubscriptionLinkAsync(
            SubscriptionLinkEditRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryDeleteSubscriptionLinkAsync(
            string linkId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ProfileImportResult> ImportSubscriptionLinkAsync(
            ProfileSubscriptionLink link,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingPageLog : IPageLog
    {
        public List<LogEntry> Entries { get; } = [];

        public void Append(
            string level,
            string category,
            string message,
            string? detail)
        {
            Entries.Add(new LogEntry(level, category, message, detail));
        }
    }

    private sealed record LogEntry(
        string Level,
        string Category,
        string Message,
        string? Detail);
}

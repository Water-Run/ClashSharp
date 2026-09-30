using System.Collections.Specialized;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Verifies profile and subscription mutations respect their owning page lifetime.</summary>
public sealed class ProfileAndLinkLifecycleViewModelTests
{
    [Fact]
    public async Task RemainingPages_LinksReplacementRejectsAnOldRowEvenWhenEveryValueMatches()
    {
        FakeSubscriptionLinkCatalog catalog = new() { Links = [CreateLink("same-id")] };
        LinksViewModel viewModel = CreateLinksViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        ProfileSubscriptionLinkDisplay oldRow = Assert.Single(viewModel.SubscriptionLinks);
        viewModel.SelectedLink = oldRow;

        await viewModel.ReloadForDataChangeAsync(CancellationToken.None);

        Assert.Null(viewModel.SelectedLink);
        Assert.Equal(oldRow, Assert.Single(viewModel.SubscriptionLinks));
        Assert.False(viewModel.IsCurrentLink(oldRow));
        viewModel.SelectedLink = oldRow;
        await viewModel.DeleteSelectedLinkAsync(CancellationToken.None);
        await viewModel.CheckSelectedLinkAsync(CancellationToken.None);
        await viewModel.UpdateSelectedLinkAsync(CancellationToken.None);
        Assert.Empty(catalog.DeletedIds);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task RemainingPages_LinksDeleteUsesTheConfirmedRowInsteadOfALaterSelection()
    {
        FakeSubscriptionLinkCatalog catalog = new() { Links = [CreateLink("confirmed"), CreateLink("selected-later")] };
        LinksViewModel viewModel = CreateLinksViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        ProfileSubscriptionLinkDisplay confirmed = viewModel.SubscriptionLinks[0];
        viewModel.SelectedLink = viewModel.SubscriptionLinks[1];

        await viewModel.DeleteLinkAsync(confirmed, CancellationToken.None);

        Assert.Equal("confirmed", Assert.Single(catalog.DeletedIds));
        Assert.Equal("selected-later", Assert.Single(viewModel.SubscriptionLinks).Model.Id);
    }

    [Fact]
    public async Task RemainingPages_LinksReplacementClearsRowsSelectionAndFeedbackBeforeAFailedRead()
    {
        FakeSubscriptionLinkCatalog catalog = new() { Links = [CreateLink("old")] };
        LinksViewModel viewModel = CreateLinksViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.DeleteLinkAsync(Assert.Single(viewModel.SubscriptionLinks), CancellationToken.None);
        Assert.True(viewModel.HasStatusText);
        catalog.Links = [CreateLink("retained")];
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedLink = Assert.Single(viewModel.SubscriptionLinks);
        catalog.Read = () => throw new IOException("replacement unavailable");

        await viewModel.ReloadForDataChangeAsync(CancellationToken.None);

        Assert.Empty(viewModel.SubscriptionLinks);
        Assert.Null(viewModel.SelectedLink);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task RemainingPages_LinksDiscardAReadFromThePreviousDirectory()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSubscriptionLinkCatalog catalog = new()
        {
            Read = () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException(); }
            return [CreateLink("old")];
        }
        };
        LinksViewModel viewModel = CreateLinksViewModel(catalog, new RecordingPageLog());
        Task old = viewModel.LoadAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            catalog.Read = () => [CreateLink("new")];
            await viewModel.ReloadForDataChangeAsync(CancellationToken.None);
        }
        finally { release.Set(); }
        await old;
        Assert.Equal("new", Assert.Single(viewModel.SubscriptionLinks).Model.Id);
    }

    [Fact]
    public async Task LoadAsync_RefreshesAndReordersProfilesWithoutResettingRetainedRows()
    {
        ConfigurationProfile first = CreateProfile("first", false);
        ConfigurationProfile second = CreateProfile("second", true);
        FakeProfileManagementCatalog catalog = new() { Profiles = [first, second] };
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        var collection = viewModel.Profiles;
        ConfigurationProfileDisplay selected = collection[1];
        viewModel.SelectedProfile = selected;
        List<NotifyCollectionChangedAction> changes = [];
        ((INotifyCollectionChanged)collection).CollectionChanged += (_, args) => changes.Add(args.Action);
        List<string?> rowChanges = [];
        selected.PropertyChanged += (_, args) => rowChanges.Add(args.PropertyName);

        catalog.Profiles = [first, second with { Name = "Renamed", Status = "Validated", NodeCount = 4 }];
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Same(collection, viewModel.Profiles);
        Assert.Same(selected, viewModel.SelectedProfile);
        Assert.Empty(changes);
        Assert.Equal("Renamed", selected.NameDisplay);
        Assert.Equal("Validated", selected.StatusDisplay);
        Assert.Equal(4, selected.NodeCount);
        Assert.Contains(nameof(ConfigurationProfileDisplay.NameDisplay), rowChanges);
        Assert.Contains(nameof(ConfigurationProfileDisplay.StatusDisplay), rowChanges);
        Assert.Contains(nameof(ConfigurationProfileDisplay.NodeCount), rowChanges);
        Assert.Equal("Renamed - Validated", viewModel.ActiveProfileText);

        catalog.Profiles = [catalog.Profiles[1], first, CreateProfile("new", false)];
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Same(selected, viewModel.Profiles[0]);
        Assert.Same(selected, viewModel.SelectedProfile);
        Assert.Equal(["second", "first", "new"], viewModel.Profiles.Select(row => row.Id));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Replace, changes);

        catalog.Profiles = [first];
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal("first", Assert.Single(viewModel.Profiles).Id);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }

    /// <summary>Rejected mutations and operational failures never leave a misleading success result.</summary>
    [Theory]
    [InlineData("activate", false)]
    [InlineData("activate", true)]
    [InlineData("rename", false)]
    [InlineData("rename", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    public async Task ProfileMutation_WhenRejectedOrFailed_ReportsFailure(string operation, bool throws)
    {
        Task<bool> Reject() => throws
            ? Task.FromException<bool>(new IOException("private configuration path"))
            : Task.FromResult(false);
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("retained", isActive: true)],
            SetActiveProfile = (_, _) => Reject(),
            RenameProfile = (_, _, _) => Reject(),
            DeleteProfile = (_, _) => Reject(),
        };
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = Assert.Single(viewModel.Profiles);

        await (operation switch
        {
            "activate" => viewModel.SetSelectedProfileActiveAsync(CancellationToken.None),
            "rename" => viewModel.RenameProfileAsync("retained", "renamed", CancellationToken.None),
            "delete" => viewModel.DeleteProfileAsync("retained", CancellationToken.None),
            _ => throw new InvalidOperationException(),
        });

        Assert.True(viewModel.HasStatusText);
        Assert.Equal(PageStatusSeverity.Error, viewModel.StatusSeverity);
        Assert.Equal($"Profiles.Status.{char.ToUpperInvariant(operation[0])}{operation[1..]}Failed", viewModel.StatusText);
        Assert.Equal("retained", Assert.Single(viewModel.Profiles).Id);
        Assert.Same(viewModel.Profiles[0], viewModel.SelectedProfile);
    }

    [Fact]
    public async Task RollbackProfileAsync_ReportsSuccessFailureAndCancellationWithoutStaleFeedback()
    {
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("retained", isActive: true)],
            RollbackProfile = (_, _) => Task.FromResult(CreateImportResult("retained")),
        };
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, new RecordingPageLog());
        ProfileHistoryEntry entry = new("version", "retained", DateTimeOffset.Now, "source", 1, 2,
            new string('a', 64), ProfileHistoryApplyOutcome.Stored);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = Assert.Single(viewModel.Profiles);
        await viewModel.RollbackProfileAsync(entry, CancellationToken.None);
        Assert.Equal("Profiles.Status.Restored", viewModel.StatusText);
        Assert.Equal(PageStatusSeverity.Success, viewModel.StatusSeverity);
        Assert.Equal(2, catalog.GetProfilesCallCount);
        Assert.Same(viewModel.Profiles[0], viewModel.SelectedProfile);

        catalog.RollbackProfile = (_, _) => Task.FromException<ProfileImportResult>(new IOException("private file path"));
        await viewModel.RollbackProfileAsync(entry, CancellationToken.None);
        Assert.Equal("Profiles.Status.RestoreFailed", viewModel.StatusText);
        Assert.Equal(PageStatusSeverity.Error, viewModel.StatusSeverity);
        Assert.Equal(2, catalog.GetProfilesCallCount);

        using CancellationTokenSource lifetime = new();
        catalog.RollbackProfile = (_, token) =>
        {
            lifetime.Cancel();
            return Task.FromCanceled<ProfileImportResult>(token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewModel.RollbackProfileAsync(entry, lifetime.Token));
        Assert.False(viewModel.HasStatusText);
        Assert.Equal(PageStatusSeverity.Informational, viewModel.StatusSeverity);
        Assert.Equal(2, catalog.GetProfilesCallCount);
    }

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
        Assert.Equal("Profiles.Status.Imported", viewModel.StatusText);
        Assert.Equal(PageStatusSeverity.Success, viewModel.StatusSeverity);
        Assert.Equal(2, catalog.GetProfilesCallCount);
        Assert.Contains(
            log.Entries,
            entry => entry.Message.Contains("Local profile imported", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportLocalProfileAsync_FailureThenRetry_UsesMatchingFeedback(bool missingSections)
    {
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("retained", isActive: true)],
            ImportLocalProfile = (_, _) => Task.FromException<ProfileImportResult>(missingSections
                ? new ProfileValidationException(MissingProfileSections.ProxyGroups | MissingProfileSections.Rules)
                : new IOException("private file path")),
        };
        ProfilesViewModel viewModel = new(
            key => key == "Profiles.Validation.MissingSections" ? "Missing: {0}" : key,
            catalog, new RecordingPageLog(), () => "retained", new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
        await viewModel.LoadAsync(CancellationToken.None);
        List<string?> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await viewModel.ImportLocalProfileAsync("profile.yaml", CancellationToken.None);

        Assert.Equal(PageStatusSeverity.Error, viewModel.StatusSeverity);
        Assert.Equal(missingSections ? "Missing: proxy-groups, rules" : "Profiles.Status.ImportFailed", viewModel.StatusText);
        Assert.DoesNotContain("private", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(1, catalog.GetProfilesCallCount);
        Assert.Contains(nameof(ProfilesViewModel.StatusSeverity), changed);

        catalog.ImportLocalProfile = (_, _) => Task.FromResult(CreateImportResult("retained"));
        await viewModel.ImportLocalProfileAsync("profile.yaml", CancellationToken.None);

        Assert.Equal(PageStatusSeverity.Success, viewModel.StatusSeverity);
        Assert.Equal("Profiles.Status.Imported", viewModel.StatusText);
        Assert.Equal(2, catalog.GetProfilesCallCount);
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
        Assert.False(viewModel.HasStatusText);
        Assert.Equal(1, catalog.GetProfilesCallCount);
        Assert.DoesNotContain(
            log.Entries,
            entry => entry.Message.Contains("Local profile imported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateSelectedProfileAsync_RetainsRowsAndSelectionBeforeActivation()
    {
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("candidate", isActive: false)],
            ValidateProfile = static (profile, _) => Task.FromResult(CreateImportResult(profile.Id)),
        };
        string? activatedProfileId = null;
        catalog.SetActiveProfile = (profileId, _) =>
        {
            activatedProfileId = profileId;
            catalog.Profiles = [CreateProfile(profileId, isActive: true)];
            return Task.FromResult(true);
        };
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        ConfigurationProfileDisplay originalRow = Assert.Single(viewModel.Profiles);
        viewModel.SelectedProfile = originalRow;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProfilesViewModel.Profiles))
            {
                viewModel.SelectedProfile = null;
            }
        };

        await viewModel.ValidateSelectedProfileAsync(CancellationToken.None);

        Assert.Same(Assert.Single(viewModel.Profiles), viewModel.SelectedProfile);
        Assert.Same(originalRow, viewModel.SelectedProfile);
        Assert.Equal("Profiles.Status.Validated", viewModel.StatusText);
        await viewModel.SetSelectedProfileActiveAsync(CancellationToken.None);
        Assert.Equal("candidate", activatedProfileId);
        Assert.True(Assert.IsType<ConfigurationProfileDisplay>(viewModel.SelectedProfile).IsActive);
    }

    [Fact]
    public async Task LoadAsync_WhenSelectedProfileDisappears_DoesNotActivateAnotherProfile()
    {
        FakeProfileManagementCatalog catalog = new()
        {
            Profiles = [CreateProfile("removed", isActive: false)],
        };
        ProfilesViewModel viewModel = CreateProfilesViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = Assert.Single(viewModel.Profiles);
        catalog.Profiles = [CreateProfile("remaining", isActive: true)];

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.SetSelectedProfileActiveAsync(CancellationToken.None);

        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal("remaining", Assert.Single(viewModel.Profiles).Id);
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
        Assert.Equal("Profiles.Status.Activated", viewModel.StatusText);
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
        Assert.False(viewModel.HasStatusText);
        Assert.Equal(1, catalog.GetProfilesCallCount);
    }

    [Fact]
    public async Task LoadAsync_WhenSubscriptionListResetsSelection_RetainsTheSameTarget()
    {
        FakeSubscriptionLinkCatalog catalog = new() { Links = [CreateLink("selected")] };
        LinksViewModel viewModel = CreateLinksViewModel(catalog, new RecordingPageLog());
        await viewModel.LoadAsync(CancellationToken.None);
        ProfileSubscriptionLinkDisplay originalRow = Assert.Single(viewModel.SubscriptionLinks);
        viewModel.SelectedLink = originalRow;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LinksViewModel.SubscriptionLinks))
            {
                viewModel.SelectedLink = null;
            }
        };

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Same(Assert.Single(viewModel.SubscriptionLinks), viewModel.SelectedLink);
        Assert.NotSame(originalRow, viewModel.SelectedLink);
        Assert.True(viewModel.HasSelectedLink);
        catalog.Links = [CreateLink("another")];
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Null(viewModel.SelectedLink);
        Assert.False(viewModel.HasSelectedLink);
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

        public Func<ConfigurationProfile, CancellationToken, Task<ProfileImportResult>> ValidateProfile { get; set; } =
            static (_, _) => throw new NotSupportedException();

        public Func<string, string, CancellationToken, Task<bool>> RenameProfile { get; set; } =
            static (_, _, _) => throw new NotSupportedException();

        public Func<string, CancellationToken, Task<bool>> DeleteProfile { get; set; } =
            static (_, _) => throw new NotSupportedException();

        public Func<ProfileHistoryEntry, CancellationToken, Task<ProfileImportResult>> RollbackProfile { get; set; } =
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
            return ValidateProfile(profile, cancellationToken);
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
            return RenameProfile(profileId, name, cancellationToken);
        }

        public Task<bool> TryDeleteProfileAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            return DeleteProfile(profileId, cancellationToken);
        }

        public Task<ProfileImportResult> RollbackProfileAsync(
            ProfileHistoryEntry historyEntry,
            CancellationToken cancellationToken)
        {
            return RollbackProfile(historyEntry, cancellationToken);
        }
    }

    private sealed class FakeSubscriptionLinkCatalog : ISubscriptionLinkCatalog
    {
        public IReadOnlyList<ProfileSubscriptionLink> Links { get; set; } = [];
        public Func<IReadOnlyList<ProfileSubscriptionLink>>? Read { get; set; }
        public List<string> DeletedIds { get; } = [];

        public int GetLinksCallCount { get; private set; }

        public Func<string, string, CancellationToken, Task<ProfileSubscriptionLink>> AddSubscriptionLink { get; set; } =
            static (_, _, _) => throw new NotSupportedException();

        public IReadOnlyList<ProfileSubscriptionLink> GetSubscriptionLinks()
        {
            GetLinksCallCount++;
            return Read?.Invoke() ?? Links;
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
            DeletedIds.Add(linkId);
            Links = Links.Where(link => link.Id != linkId).ToArray();
            return Task.FromResult(true);
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

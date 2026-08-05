using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for profile catalog composition.</summary>
public sealed class ProfileCatalogServiceTests
{
    [Fact]
    public async Task MutationCoordinator_QueuesBehindProcessWideFairGate()
    {
        MutationAdmissionBarrier barrier = new();
        FairAsyncMutationGate gate = new();
        ProfileCatalogMutationCoordinator coordinator = new(barrier, gate);
        TaskCompletionSource gateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> blocker = gate.ExecuteAsync(
            Guid.NewGuid(),
            async (_, _) =>
            {
                gateEntered.SetResult();
                await releaseGate.Task;
                return true;
            },
            CancellationToken.None);
        await gateEntered.Task;

        Task<bool> queued = coordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.False(queued.IsCompleted);
        releaseGate.SetResult();
        Assert.True(await queued);
        Assert.True(await blocker);
    }

    [Fact]
    public void Constructor_DoesNotCreateCatalogDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "clashsharp-profile-constructor-" + Guid.NewGuid().ToString("N"));
        string catalogPath = Path.Combine(root, "nested", "ProfileCatalog.json");

        _ = CreateService(catalogPath, new FakeProfileCatalogSettings());

        Assert.False(Directory.Exists(root));
    }

    /// <summary>Verifies a missing catalog creates the localized built-in profile and marks it active from injected settings.</summary>
    [Fact]
    public void GetProfiles_WhenCatalogMissing_ReturnsLocalizedBuiltInProfile()
    {
        using TempFile tempFile = new();
        FakeProfileCatalogSettings settings = new()
        {
            ActiveProfileId = ProfileCatalogIds.BuiltInDirect,
        };
        ProfileCatalogService service = CreateService(tempFile.Path, settings);

        ConfigurationProfile profile = Assert.Single(service.GetProfiles());

        Assert.Equal(ProfileCatalogIds.BuiltInDirect, profile.Id);
        Assert.Equal("localized direct", profile.Name);
        Assert.Equal("localized available", profile.Status);
        Assert.True(profile.IsActive);
    }

    /// <summary>Verifies activating an existing profile writes through the injected settings store.</summary>
    [Fact]
    public async Task TrySetActiveProfile_WhenProfileExists_UpdatesInjectedSettings()
    {
        using TempFile tempFile = new();
        FakeProfileCatalogSettings settings = new();
        ProfileCatalogService service = CreateService(tempFile.Path, settings);

        bool updated = await service.TryApplyActiveProfileAsync(
            ProfileCatalogIds.BuiltInDirect,
            CancellationToken.None);

        Assert.True(updated);
        Assert.Equal(ProfileCatalogIds.BuiltInDirect, settings.ActiveProfileId);
    }

    /// <summary>Verifies subscription link edit, scheduling, disable, and delete operations persist coherently.</summary>
    [Fact]
    public async Task SubscriptionLinkCrud_UpdatesDueSelectionAndDeletesRow()
    {
        using TempFile tempFile = new();
        ProfileCatalogService service = CreateService(tempFile.Path, new FakeProfileCatalogSettings());
        ProfileSubscriptionLink link = await service.AddSubscriptionLinkAsync(
            "Primary",
            "https://example.com/sub",
            CancellationToken.None);

        bool updated = await service.TryUpdateSubscriptionLinkAsync(
            link.Id,
            "Renamed",
            "https://example.com/renamed",
            isEnabled: true,
            updateIntervalHours: 2,
            cancellationToken: CancellationToken.None);

        Assert.True(updated);
        ProfileSubscriptionLink persisted = Assert.Single(service.GetSubscriptionLinks());
        Assert.Equal("Renamed", persisted.Name);
        Assert.Equal("https://example.com/renamed", persisted.Uri);
        Assert.Equal(2, persisted.UpdateIntervalHours);
        Assert.Single(service.GetDueSubscriptionLinks(DateTimeOffset.Now.AddMinutes(1)));

        Assert.True(await service.TryDeleteSubscriptionLinkAsync(link.Id, CancellationToken.None));
        Assert.Empty(service.GetSubscriptionLinks());
        Assert.False(await service.TryDeleteSubscriptionLinkAsync(link.Id, CancellationToken.None));
    }

    /// <summary>Verifies reachability/status checks do not rewrite the last successful update time.</summary>
    [Fact]
    public async Task TryUpdateSubscriptionLinkStatus_DoesNotChangeLastSuccessfulTimestamp()
    {
        using TempFile tempFile = new();
        ProfileCatalogService service = CreateService(tempFile.Path, new FakeProfileCatalogSettings());
        ProfileSubscriptionLink link = await service.AddSubscriptionLinkAsync(
            "Primary",
            "https://example.com/sub",
            CancellationToken.None);

        Assert.True(await service.TryUpdateSubscriptionLinkStatusAsync(
            link.Id,
            "checked",
            CancellationToken.None));

        ProfileSubscriptionLink persisted = Assert.Single(service.GetSubscriptionLinks());
        Assert.Equal(link.LastUpdatedAt, persisted.LastUpdatedAt);
        Assert.Equal("checked", persisted.Status);
    }

    /// <summary>Verifies overdue links catch up, failures retry with backoff, and success returns to the user interval.</summary>
    [Fact]
    public async Task SubscriptionSchedule_PersistsBackoffAndResetsAfterSuccess()
    {
        using TempFile tempFile = new();
        ProfileCatalogService service = CreateService(tempFile.Path, new FakeProfileCatalogSettings());
        ProfileSubscriptionLink link = await service.AddSubscriptionLinkAsync(
            "Primary",
            "https://example.com/sub",
            CancellationToken.None);
        Assert.True(await service.TryUpdateSubscriptionLinkAsync(
            link.Id,
            link.Name,
            link.Uri,
            true,
            24,
            CancellationToken.None));
        _ = Assert.Single(service.GetSubscriptionLinks());
        DateTimeOffset overdueAt = DateTimeOffset.Now.AddDays(2);
        Assert.Single(service.GetDueSubscriptionLinks(overdueAt));

        await service.RecordSubscriptionUpdateOutcomeAsync(
            link.Id,
            succeeded: false,
            attemptedAt: overdueAt,
            cancellationToken: CancellationToken.None);
        Assert.Empty(service.GetDueSubscriptionLinks(overdueAt.AddMinutes(4)));
        Assert.Single(service.GetDueSubscriptionLinks(overdueAt.AddMinutes(5)));

        DateTimeOffset secondAttempt = overdueAt.AddMinutes(5);
        await service.RecordSubscriptionUpdateOutcomeAsync(
            link.Id,
            succeeded: false,
            attemptedAt: secondAttempt,
            cancellationToken: CancellationToken.None);
        Assert.Empty(service.GetDueSubscriptionLinks(secondAttempt.AddMinutes(9)));
        Assert.Single(service.GetDueSubscriptionLinks(secondAttempt.AddMinutes(10)));

        DateTimeOffset succeededAt = secondAttempt.AddMinutes(10);
        await service.RecordSubscriptionUpdateOutcomeAsync(
            link.Id,
            succeeded: true,
            attemptedAt: succeededAt,
            cancellationToken: CancellationToken.None);
        Assert.Empty(service.GetDueSubscriptionLinks(succeededAt.AddHours(23)));
        Assert.Single(service.GetDueSubscriptionLinks(succeededAt.AddHours(24)));
    }

    /// <summary>Verifies successful imports create rollback versions and rollback creates a new linear version.</summary>
    [Fact]
    public async Task ImportAndRollback_CreateImmutableProfileHistory()
    {
        using TempFile tempFile = new();
        await File.WriteAllTextAsync(tempFile.Path, "proxies: []\nrules: []\n");
        FakeProfileCatalogCoreConfiguration core = new();
        ProfileCatalogService service = CreateService(tempFile.Path + ".catalog", new FakeProfileCatalogSettings(), core);

        ProfileImportResult imported = await service.ImportLocalProfileAsync(tempFile.Path, CancellationToken.None);
        ProfileHistoryEntry originalVersion = Assert.Single(service.GetProfileHistory(imported.ProfileId));
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("proxies: []\nrules: []\n"))),
            originalVersion.ContentSha256);
        Assert.Equal(ProfileHistoryApplyOutcome.Stored, originalVersion.ApplyOutcome);

        ProfileImportResult rolledBack = await service.RollbackProfileAsync(originalVersion, CancellationToken.None);

        Assert.Equal(imported.ProfileId, rolledBack.ProfileId);
        Assert.Equal(2, service.GetProfileHistory(imported.ProfileId).Count);
        Assert.Equal(
            ProfileHistoryApplyOutcome.RollbackApplied,
            service.GetProfileHistory(imported.ProfileId)[0].ApplyOutcome);
        Assert.Equal(2, core.Imports.Count);
        Assert.Equal("proxies: []\nrules: []\n", core.Imports[1].ConfigurationText);
    }

    /// <summary>Verifies deleting an active user profile falls back safely and removes retained history.</summary>
    [Fact]
    public async Task TryDeleteProfile_WhenProfileIsActive_FallsBackAndRemovesHistory()
    {
        using TempFile tempFile = new();
        await File.WriteAllTextAsync(tempFile.Path, "proxies: []\n");
        FakeProfileCatalogSettings settings = new();
        FakeProfileCatalogRuntime runtime = new();
        ProfileCatalogService service = CreateService(
            tempFile.Path + ".catalog",
            settings,
            new FakeProfileCatalogCoreConfiguration(),
            runtime);
        ProfileImportResult imported = await service.ImportLocalProfileAsync(tempFile.Path, CancellationToken.None);
        Assert.True(await service.TryApplyActiveProfileAsync(imported.ProfileId, CancellationToken.None));
        Assert.True(await service.TryRenameProfileAsync(imported.ProfileId, "Renamed profile", CancellationToken.None));

        bool deleted = await service.TryDeleteProfileAsync(imported.ProfileId, CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal(ProfileCatalogIds.BuiltInDirect, settings.ActiveProfileId);
        Assert.Equal(
            [imported.ProfileId, ProfileCatalogIds.BuiltInDirect],
            runtime.AppliedProfileIds);
        Assert.Single(service.GetProfiles());
        Assert.Empty(service.GetProfileHistory(imported.ProfileId));
        Assert.False(await service.TryDeleteProfileAsync(
            ProfileCatalogIds.BuiltInDirect,
            CancellationToken.None));
    }

    /// <summary>Verifies the legacy active pointer is committed only after runtime readiness succeeds.</summary>
    [Fact]
    public async Task TryApplyActiveProfileAsync_WhenRuntimeFails_PreservesActivePointer()
    {
        using TempFile tempFile = new();
        await File.WriteAllTextAsync(tempFile.Path, "proxies: []\n");
        FakeProfileCatalogSettings settings = new()
        {
            ActiveProfileId = ProfileCatalogIds.BuiltInDirect,
        };
        FakeProfileCatalogRuntime runtime = new() { ApplyResult = false };
        ProfileCatalogService service = CreateService(
            tempFile.Path + ".catalog",
            settings,
            new FakeProfileCatalogCoreConfiguration(),
            runtime);
        ProfileImportResult imported = await service.ImportLocalProfileAsync(tempFile.Path, CancellationToken.None);

        bool activated = await service.TryApplyActiveProfileAsync(imported.ProfileId, CancellationToken.None);

        Assert.False(activated);
        Assert.Equal(ProfileCatalogIds.BuiltInDirect, settings.ActiveProfileId);
        Assert.Equal(imported.ProfileId, Assert.Single(runtime.AppliedProfileIds));
    }

    [Fact]
    public async Task AddSubscriptionLinkAsync_WhenCatalogSaveFails_DoesNotPublishGhostLink()
    {
        using TempFile tempFile = new();
        ProfileCatalogService service = CreateService(
            tempFile.Path,
            new FakeProfileCatalogSettings());
        _ = service.GetProfiles();
        File.Delete(tempFile.Path);
        Directory.CreateDirectory(tempFile.Path);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => service.AddSubscriptionLinkAsync(
            "Ghost",
            "https://example.com/ghost",
            CancellationToken.None));
        Assert.True(failure is IOException or UnauthorizedAccessException);

        Assert.Empty(service.GetSubscriptionLinks());
    }

    [Fact]
    public async Task ImportSubscriptionLinkAsync_WhenUriRevisionIsStale_RejectsBeforeImport()
    {
        using TempFile tempFile = new();
        FakeProfileCatalogCoreConfiguration core = new();
        ProfileCatalogService service = CreateService(
            tempFile.Path,
            new FakeProfileCatalogSettings(),
            core);
        ProfileSubscriptionLink stale = await service.AddSubscriptionLinkAsync(
            "Primary",
            "https://example.com/original",
            CancellationToken.None);
        Assert.True(await service.TryUpdateSubscriptionLinkAsync(
            stale.Id,
            stale.Name,
            "https://example.com/revised",
            isEnabled: true,
            updateIntervalHours: 24,
            cancellationToken: CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportSubscriptionLinkAsync(stale, CancellationToken.None));
        ProfileImportResult? scheduled = await service.ImportDueSubscriptionLinkAsync(
            stale,
            DateTimeOffset.Now.AddDays(1),
            CancellationToken.None);

        Assert.Null(scheduled);
        Assert.Empty(core.Imports);
        Assert.True(Assert.Single(service.GetSubscriptionLinks()).Revision > stale.Revision);
    }

    [Fact]
    public async Task ImportLocalProfileAsync_WhenFileExceedsLimit_RejectsBeforeCoreImport()
    {
        using TempFile tempFile = new();
        await File.WriteAllBytesAsync(tempFile.Path, new byte[(4 * 1024 * 1024) + 1]);
        FakeProfileCatalogCoreConfiguration core = new();
        ProfileCatalogService service = CreateService(
            tempFile.Path + ".catalog",
            new FakeProfileCatalogSettings(),
            core);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportLocalProfileAsync(tempFile.Path, CancellationToken.None));

        Assert.Empty(core.Imports);
    }

    [Fact]
    public async Task ImportLocalProfileAsync_WhenUtf8IsInvalid_RejectsBeforeCoreImport()
    {
        using TempFile tempFile = new();
        await File.WriteAllBytesAsync(tempFile.Path, [0xC3, 0x28]);
        FakeProfileCatalogCoreConfiguration core = new();
        ProfileCatalogService service = CreateService(
            tempFile.Path + ".catalog",
            new FakeProfileCatalogSettings(),
            core);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportLocalProfileAsync(tempFile.Path, CancellationToken.None));

        Assert.Empty(core.Imports);
    }

    [Fact]
    public async Task TryDeleteProfileAsync_WhenCatalogSaveFails_RestoresPointerAndRuntime()
    {
        using TempFile tempFile = new();
        await File.WriteAllTextAsync(tempFile.Path, "proxies: []\n");
        FakeProfileCatalogSettings settings = new();
        FakeProfileCatalogRuntime runtime = new();
        string catalogPath = tempFile.Path + ".catalog";
        ProfileCatalogService service = CreateService(
            catalogPath,
            settings,
            new FakeProfileCatalogCoreConfiguration(),
            runtime);
        ProfileImportResult imported = await service.ImportLocalProfileAsync(
            tempFile.Path,
            CancellationToken.None);
        Assert.True(await service.TryApplyActiveProfileAsync(imported.ProfileId, CancellationToken.None));
        File.Delete(catalogPath);
        Directory.CreateDirectory(catalogPath);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.TryDeleteProfileAsync(imported.ProfileId, CancellationToken.None));
        Assert.True(failure is IOException or UnauthorizedAccessException);

        Assert.Equal(imported.ProfileId, settings.ActiveProfileId);
        Assert.Equal(
            [imported.ProfileId, ProfileCatalogIds.BuiltInDirect, imported.ProfileId],
            runtime.AppliedProfileIds);
        Assert.Contains(service.GetProfiles(), profile => profile.Id == imported.ProfileId);
    }

    [Fact]
    public async Task TryDeleteProfileAsync_WhenPointerCommitFails_RestoresPointerAndRuntime()
    {
        using TempFile tempFile = new();
        await File.WriteAllTextAsync(tempFile.Path, "proxies: []\n");
        FakeProfileCatalogSettings settings = new();
        FakeProfileCatalogRuntime runtime = new();
        ProfileCatalogService service = CreateService(
            tempFile.Path + ".catalog",
            settings,
            new FakeProfileCatalogCoreConfiguration(),
            runtime);
        ProfileImportResult imported = await service.ImportLocalProfileAsync(
            tempFile.Path,
            CancellationToken.None);
        Assert.True(await service.TryApplyActiveProfileAsync(imported.ProfileId, CancellationToken.None));
        settings.FailNextValue = ProfileCatalogIds.BuiltInDirect;

        await Assert.ThrowsAsync<IOException>(() =>
            service.TryDeleteProfileAsync(imported.ProfileId, CancellationToken.None));

        Assert.Equal(imported.ProfileId, settings.ActiveProfileId);
        Assert.Equal(
            [imported.ProfileId, ProfileCatalogIds.BuiltInDirect, imported.ProfileId],
            runtime.AppliedProfileIds);
        Assert.Contains(service.GetProfiles(), profile => profile.Id == imported.ProfileId);
    }

    [Fact]
    public async Task TryDeleteProfileAsync_WhenCleanupFails_PersistsRetryTombstone()
    {
        using TempFile tempFile = new();
        await File.WriteAllTextAsync(tempFile.Path, "proxies: []\n");
        FakeProfileCatalogRuntime runtime = new()
        {
            DeleteException = new IOException("locked"),
        };
        ProfileCatalogService service = CreateService(
            tempFile.Path + ".catalog",
            new FakeProfileCatalogSettings(),
            new FakeProfileCatalogCoreConfiguration(),
            runtime);
        ProfileImportResult imported = await service.ImportLocalProfileAsync(
            tempFile.Path,
            CancellationToken.None);

        Assert.True(await service.TryDeleteProfileAsync(imported.ProfileId, CancellationToken.None));
        Assert.Equal(1, runtime.DeleteCallCount);

        runtime.DeleteException = null;
        service.ResetAfterDataDeletion();
        await service.RetryPendingProfileCleanupAsync(CancellationToken.None);
        Assert.Equal(2, runtime.DeleteCallCount);
        await service.RetryPendingProfileCleanupAsync(CancellationToken.None);
        Assert.Equal(2, runtime.DeleteCallCount);
    }

    private static ProfileCatalogService CreateService(
        string catalogPath,
        FakeProfileCatalogSettings settings,
        FakeProfileCatalogCoreConfiguration? core = null,
        FakeProfileCatalogRuntime? runtime = null)
    {
        return new ProfileCatalogService(
            catalogPath,
            Path.Combine(Path.GetDirectoryName(catalogPath)!, "mihomo", "history"),
            settings,
            core ?? new FakeProfileCatalogCoreConfiguration(),
            runtime ?? new FakeProfileCatalogRuntime(),
            new FakeProfileCatalogLog(),
            key => key switch
            {
                "ProfileCatalog.BuiltInDirect.Name" => "localized direct",
                "ProfileCatalog.Status.Available" => "localized available",
                _ => key,
            },
            UncoordinatedProfileCatalogMutationCoordinator.Instance);
    }

    private sealed class FakeProfileCatalogSettings : IProfileCatalogSettings
    {
        private string _activeProfileId = string.Empty;

        public string? FailNextValue { get; set; }

        public string ActiveProfileId
        {
            get => _activeProfileId;
            set
            {
                _activeProfileId = value;
                if (StringComparer.Ordinal.Equals(FailNextValue, value))
                {
                    FailNextValue = null;
                    throw new IOException("simulated settings failure");
                }
            }
        }
    }

    private sealed class FakeProfileCatalogCoreConfiguration : IProfileCatalogCoreConfiguration
    {
        public List<ImportCall> Imports { get; } = [];

        public Task<ProfileImportResult> ImportProfileConfigurationAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken)
        {
            Imports.Add(new ImportCall(profileId, profileName, configurationText));
            return Task.FromResult(new ProfileImportResult(
                profileId,
                profileName,
                profileId + ".yaml",
                2,
                3,
                "validated"));
        }

        public CoreConfigurationState EnsureDefaultConfiguration()
        {
            throw new NotSupportedException();
        }

        public Task<string?> ReadImportedProfileConfigurationAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            string? configuration = Imports
                .LastOrDefault(importCall => StringComparer.Ordinal.Equals(importCall.ProfileId, profileId))
                .ConfigurationText;
            return Task.FromResult<string?>(configuration);
        }

        public Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public readonly record struct ImportCall(string ProfileId, string ProfileName, string ConfigurationText);
    }

    private sealed class FakeProfileCatalogLog : IProfileCatalogLog
    {
        public void AppendLog(string level, string category, string message, string? detail)
        {
        }
    }

    private sealed class FakeProfileCatalogRuntime : IProfileCatalogRuntime
    {
        public bool ApplyResult { get; set; } = true;

        public Exception? DeleteException { get; set; }

        public int DeleteCallCount { get; private set; }

        public List<string> AppliedProfileIds { get; } = [];

        public Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken)
        {
            AppliedProfileIds.Add(profileId);
            return Task.FromResult(ApplyResult);
        }

        public Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProfileCatalogRuntimeImportResult(
                new ProfileImportResult(profileId, profileName, profileId + ".yaml", 2, 3, "validated"),
                true));
        }

        public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken)
        {
            DeleteCallCount++;
            if (DeleteException is Exception exception)
            {
                throw exception;
            }

            return Task.FromResult(true);
        }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clashsharp-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "ProfileCatalog.json");
        }

        public string Path { get; }

        public void Dispose()
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

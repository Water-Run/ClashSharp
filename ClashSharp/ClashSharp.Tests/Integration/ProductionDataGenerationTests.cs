extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using Factory = ClashSharpUi::ClashSharp.Hosting.Data.AppDataGenerationFactory;
using Metrics = ClashSharpUi::ClashSharp.Service.ICoreConfigurationProfileMetrics;
using ProfileRuntime = ClashSharpUi::ClashSharp.Service.IProfileCatalogRuntime;
using ProfileRuntimeImport = ClashSharpUi::ClashSharp.Service.ProfileCatalogRuntimeImportResult;
using Profiles = ClashSharpUi::ClashSharp.Service.ProfileCatalogService;
using Repositories = ClashSharpUi::ClashSharp.Hosting.Data.AppDataGenerationRepositories;
using Validator = ClashSharpUi::ClashSharp.Service.ICoreConfigurationValidator;

namespace ClashSharp.Tests.Integration;

/// <summary>Runs the actual production repository composition in temporary directories with isolated native-effect boundaries.</summary>
public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task FirstGeneration_OwnsAllRealRepositories_AndRestartReusesThemWithoutLegacyRecovery()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.Recover = async () =>
        {
            Directory.CreateDirectory(directory.RootPath);
            await File.WriteAllTextAsync(Path.Combine(directory.RootPath, "ProfileCatalog.json"), "{\"Profiles\":[],\"Links\":[]}");
        };
        Assert.False(Directory.Exists(directory.RootPath));
        DataGenerationManifestSnapshot manifest = await fixture.StartAsync();
        Repositories repositories = Assert.Single(fixture.Containers);
        Assert.True(manifest.Descriptor.IsSameGeneration(repositories.Generation));
        Assert.Equal(["recover", "preferences"], fixture.Calls);
        Assert.Equal(6, ((SettingsGenerationContext)repositories.GetService(typeof(SettingsGenerationContext))!).Participants.Count);
        Assert.NotEmpty(repositories.Session.Snapshot.PendingApplications);
        Assert.True(File.Exists(Path.Combine(manifest.Descriptor.RootPath, "ProfileCatalog.json")));
        Assert.StartsWith(manifest.Descriptor.RootPath, repositories.Logs.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(manifest.Descriptor.RootPath, repositories.Configuration.GetState().DirectoryPath, StringComparison.OrdinalIgnoreCase);
        repositories.Logs.AppendLog("Info", "Test", "generation-owned record", null);
        _ = await fixture.Manager.ExecuteAsync<Profiles, string>(async (profiles, _, token) =>
            (await profiles.AddSubscriptionLinkAsync("retained subscription", "https://example.test/subscription", token)).Name, CancellationToken.None);
        Assert.Equal(1, repositories.Logs.GetStorageSummary().LogCount);
        await fixture.Manager.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => repositories.Logs.GetStorageSummary());
        Assert.Throws<ObjectDisposedException>(() => repositories.Profiles.GetProfiles());
        Assert.Throws<ObjectDisposedException>(() => repositories.Session.Snapshot);
        Assert.All(fixture.Participants, participant => Assert.Equal(1, participant.Disposals));

        await using Fixture restarted = new(directory);
        restarted.Recover = () => throw new InvalidOperationException("Legacy recovery must not run after cutover.");
        DataGenerationManifestSnapshot next = await restarted.StartAsync();
        Assert.Empty(restarted.Calls);
        Assert.Equal(manifest.ContentHash, next.ContentHash);
        Repositories reopened = Assert.Single(restarted.Containers);
        Assert.Equal("generation-owned record", Assert.Single(reopened.Logs.GetRecentLogs(10)).Message);
        Assert.Equal("retained subscription", Assert.Single(reopened.Profiles.GetSubscriptionLinks()).Name);
    }

    [Theory]
    [InlineData("ProfileCatalog.json")]
    [InlineData("ClashSharpLogs.sqlite3")]
    [InlineData("Triggers.db")]
    [InlineData("Settings/v1/settings-envelope.json")]
    public async Task MissingPublishedRepository_IsNotSilentlyRecreatedOrRemigrated(string relativePath)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture initial = new(directory);
        DataGenerationManifestSnapshot manifest = await initial.StartAsync();
        await initial.Manager.DisposeAsync();
        string missingPath = Path.GetFullPath(Path.Combine(manifest.Descriptor.RootPath, relativePath));
        Assert.StartsWith(manifest.Descriptor.RootPath + Path.DirectorySeparatorChar, missingPath, StringComparison.OrdinalIgnoreCase);
        File.Delete(missingPath);
        // Remove only this test's verified settings backup too, so an intentional backup recovery cannot satisfy the read.
        if (relativePath.StartsWith("Settings/", StringComparison.Ordinal))
        {
            JsonSettingsRepository settings = new(manifest.Descriptor, SettingsRegistry.Default);
            if (File.Exists(settings.BackupPath)) { File.Delete(settings.BackupPath); }
        }
        await using Fixture reopened = new(directory);

        Assert.NotNull(await Record.ExceptionAsync(reopened.StartAsync));

        Assert.False(File.Exists(missingPath));
        Assert.Empty(reopened.Calls);
        Assert.Equal(manifest.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        Assert.Throws<DataGenerationManagerException>(() => reopened.Manager.CurrentManifest);
    }

    [Fact]
    public async Task CorruptLegacyCatalog_StopsBeforePublicationAndLeavesOriginalRecoverable()
    {
        await using DataGenerationTestDirectory directory = new();
        Directory.CreateDirectory(directory.RootPath);
        string catalog = Path.Combine(directory.RootPath, "ProfileCatalog.json");
        await File.WriteAllTextAsync(catalog, "{corrupt");
        await using Fixture fixture = new(directory);
        Assert.NotNull(await Record.ExceptionAsync(fixture.StartAsync));
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
        Assert.Equal("{corrupt", await File.ReadAllTextAsync(catalog));
        Repositories rejected = Assert.Single(fixture.Containers);
        Assert.Throws<ObjectDisposedException>(() => rejected.Session.Snapshot);
        Assert.Throws<ObjectDisposedException>(() => rejected.Logs.GetStorageSummary());
        Assert.Throws<ObjectDisposedException>(() => rejected.Profiles.GetProfiles());
        Assert.Empty(fixture.Participants);
    }

    [Theory]
    [InlineData("ClashSharpLogs.sqlite3")]
    [InlineData("Triggers.db")]
    public async Task TruncatedPublishedDatabase_IsNotInitializedAsEmpty(string name)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture initial = new(directory);
        DataGenerationManifestSnapshot manifest = await initial.StartAsync();
        await initial.Manager.DisposeAsync();
        string path = Path.Combine(manifest.Descriptor.RootPath, name);
        await File.WriteAllBytesAsync(path, []);
        await using Fixture reopened = new(directory);

        await Assert.ThrowsAsync<EndOfStreamException>(reopened.StartAsync);

        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Empty(reopened.Calls);
        Assert.Equal(manifest.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    [Fact]
    public async Task FailedParticipantConstruction_RetiresOpenedRepositoriesAndEarlierParticipants()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory, failThirdParticipant: true);
        IOException failure = await Assert.ThrowsAsync<IOException>(fixture.StartAsync);
        Assert.Equal("participant construction failure", failure.Message);
        Assert.Equal(2, fixture.Participants.Count);
        Assert.All(fixture.Participants, participant => Assert.Equal(1, participant.Disposals));
        Repositories rejected = Assert.Single(fixture.Containers);
        Assert.Throws<ObjectDisposedException>(() => rejected.Session.Snapshot);
        Assert.Throws<ObjectDisposedException>(() => rejected.Logs.GetStorageSummary());
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(rejected.Generation.RootPath, "ProfileCatalog.json")));
    }

    [Fact]
    public async Task FirstGenerationRecoveryFailure_DoesNotReadPreferencesOrAllocateCandidate()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.Recover = () => throw new IOException("retained transaction unresolved");
        await Assert.ThrowsAsync<IOException>(fixture.StartAsync);
        Assert.Equal(["recover"], fixture.Calls);
        Assert.Empty(fixture.Containers);
        Assert.Empty(Directory.GetDirectories(directory.Policy.GenerationsRootPath));
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ManagerRetirement_WaitsForCompleteOperationAgainstActualProfileAndLogRepositories()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        Repositories repositories = Assert.Single(fixture.Containers);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> accepted = fixture.Manager.ExecuteAsync<Profiles, string>(async (profiles, _, token) =>
        {
            entered.TrySetResult();
            await release.Task;
            repositories.Logs.AppendLog("Info", "Test", "accepted operation finishing", null);
            return (await profiles.AddSubscriptionLinkAsync("accepted", "https://example.test/accepted", token)).Name;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task retiring = fixture.Manager.DisposeAsync().AsTask();
        try
        {
            Assert.False(retiring.IsCompleted);
            Assert.All(fixture.Participants, participant => Assert.Equal(0, participant.Disposals));
            Assert.Throws<DataGenerationManagerException>(() => fixture.Manager.ReadSnapshot<SettingsAuthoritySession, SettingsEnvelope>((session, _) => session.Snapshot));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("accepted", await accepted);
        await retiring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => repositories.Profiles.GetProfiles());
        Assert.Throws<ObjectDisposedException>(() => repositories.Logs.GetRecentLogs(5));
        Assert.All(fixture.Participants, participant => Assert.Equal(1, participant.Disposals));
        await using Fixture reopened = new(directory);
        _ = await reopened.StartAsync();
        Assert.Equal("accepted", Assert.Single(reopened.Containers[0].Profiles.GetSubscriptionLinks()).Name);
        Assert.Equal("accepted operation finishing", Assert.Single(reopened.Containers[0].Logs.GetRecentLogs(5)).Message);
    }

    [Fact]
    public async Task FailedProducerStop_RetainsRepositoryDependenciesUntilRetrySucceeds()
    {
        await using DataGenerationTestDirectory directory = new();
        Producer producer = new();
        await using Fixture fixture = new(directory, producer: producer);
        _ = await fixture.StartAsync();
        Repositories repositories = fixture.Containers[0];
        try
        {
            AggregateException failed = await Assert.ThrowsAsync<AggregateException>(() => fixture.Manager.DisposeAsync().AsTask());
            Assert.IsType<IOException>(Assert.Single(failed.Flatten().InnerExceptions));
            Assert.All(fixture.Participants, participant => Assert.Equal(0, participant.Disposals));
            repositories.Logs.AppendLog("Info", "Test", "producer is still retiring", null);
            Assert.NotEmpty(repositories.Session.Snapshot.Desired);
        }
        finally
        {
            producer.FailStop = false;
            await fixture.Manager.DisposeAsync();
        }
        Assert.Equal(2, producer.Stops);
        Assert.All(fixture.Participants, participant => Assert.Equal(1, participant.Disposals));
        Assert.Throws<ObjectDisposedException>(() => repositories.Logs.GetRecentLogs(5));
    }

    private sealed partial class Fixture : IAsyncDisposable, ILegacySettingsSource
    {
        private readonly DataGenerationBootstrapper _bootstrap;
        private readonly DataGenerationTestDirectory _directory;
        private readonly Factory _factory;
        public MutationAdmissionBarrier Admission { get; } = new();
        public FairAsyncMutationGate MutationGate { get; } = new();
        public ConfigurationValidator Validator { get; } = new();
        public DataGenerationManager Manager { get; } = new();
        public GenerationSettingsAuthority Authority { get; }
        public Func<Repositories, CancellationToken, Task>? ComposeRuntime { get; set; }
        public Func<ClashSharpUi::ClashSharp.Service.CoreConfigurationService, ClashSharpUi::ClashSharp.Service.ProxySelectionService>? CreateProxySelections { get; set; }
        public List<string> Calls { get; } = [];
        public List<Repositories> Containers { get; } = [];
        public List<Participant> Participants { get; } = [];
        public Func<Task>? Recover { get; set; }

        public Fixture(DataGenerationTestDirectory directory, bool failThirdParticipant = false, Producer? producer = null)
        {
            _directory = directory;
            GenerationSettingsAuthority authority = new(Manager, Admission);
            Authority = authority;
            Dictionary<SettingApplicationKind, Func<Repositories, ISettingsApplicationParticipant>> participants =
                SettingsRegistry.Default.Definitions.Select(definition => definition.ApplicationKind).Distinct().ToDictionary(kind => kind,
                    kind => new Func<Repositories, ISettingsApplicationParticipant>(repositories =>
                    {
                        if (producer is not null && Participants.Count == 0) { repositories.OwnProducer(producer); }
                        if (failThirdParticipant && Participants.Count == 2) { throw new IOException("participant construction failure"); }
                        Participant participant = new(kind);
                        Participants.Add(participant);
                        return participant;
                    }));
            Factory factory = new(directory.RootPath, Admission, this, async (_, _) =>
            {
                Calls.Add("recover");
                if (Recover is not null) { await Recover(); }
            }, session =>
            {
                Repositories repositories = new(session, authority, Admission, MutationGate, new Credential(),
                    new ProfileMetrics(), Validator, key => key, (_, _) => new Runtime(), CreateProxySelections);
                Containers.Add(repositories);
                return repositories;
            }, async (repositories, token) =>
            {
                if (ComposeRuntime is not null) { await ComposeRuntime(repositories, token); return; }
                foreach (var factory in participants.OrderBy(pair => pair.Key))
                {
                    token.ThrowIfCancellationRequested();
                    repositories.OwnSettingsParticipant(factory.Value(repositories));
                }
            });
            _factory = factory;
            _bootstrap = new(directory.Store, factory, Manager, Admission);
        }

        public async Task<DataGenerationManifestSnapshot> StartAsync()
        {
            await using MutationAdmissionLease lease = await Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            return await _bootstrap.InitializeAdmittedAsync(lease, CancellationToken.None);
        }

        public Task<LegacySettingsSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls.Add("preferences");
            return Task.FromResult(new LegacySettingsSnapshot(SettingsRegistry.Default, new Dictionary<string, object?>()));
        }

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private sealed class Participant(SettingApplicationKind kind) : ISettingsApplicationParticipant, IDisposable
    {
        public SettingApplicationKind ApplicationKind => kind;
        public int Disposals { get; private set; }
        public void Dispose() => ++Disposals;
        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Opening a generation must not run a native probe.");
        public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Opening a generation must not apply native settings.");
    }

    private sealed class Producer : IRuntimeParticipant
    {
        public string Name => "test-producer";
        public bool FailStop { get; set; } = true;
        public int Stops { get; private set; }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            ++Stops;
            return FailStop ? Task.FromException(new IOException("producer stop failed")) : Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Credential : IControllerCredentialProvider
    {
        public string GetSecret() => "isolated-configuration-test-credential";
    }

    private sealed class ProfileMetrics : Metrics
    {
        public int CountNodes(string configurationText) => 0;
        public int CountRules(string configurationText) => 0;
    }

    private sealed class ConfigurationValidator : Validator
    {
        public Func<CancellationToken, Task>? OnValidate { get; set; }
        public Task ValidateAsync(string workingDirectory, string configurationPath, CancellationToken cancellationToken) =>
            OnValidate?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    private sealed class Runtime : ProfileRuntime
    {
        public Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProfileRuntimeImport> ImportAndApplyProfileAsync(string profileId, string profileName, string configurationText, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

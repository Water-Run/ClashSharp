extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.Model;
using ClashSharp.Settings;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task GenerationRemoval_ClearsCredentialsAndMigrationInputBeforeDeletingReleasedRepositories()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        fixture.LegacyValues["obsolete-extension"] = "remove this too";
        var manifest = await fixture.StartAsync();
        var logs = fixture.Containers[0].Logs;
        logs.AppendLog("Info", "Removal", "existing data", null);
        UiService.AppSettingsService settings = CreateRemovalSettings(fixture);
        RemovalCredentialStore store = new();
        using ControllerCredentialService credentials = CreateRemovalCredentials(fixture, store);
        RemovalNetwork network = new(fixture.Admission);
        var operation = CreateRemovalFactory(fixture, directory.RootPath, settings, credentials, network).Create();
        string outside = Path.Combine(Path.GetDirectoryName(directory.RootPath)!, "keep-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outside, "outside owned data");
        try
        {
            await operation.PrepareShutdownAsync(CancellationToken.None);
            Assert.Equal(ClashSharpMode.Disabled, network.LastIntent!.Mode);
            Assert.Equal(MutationAdmissionState.ClosedForShutdown, fixture.Admission.State);
            store.BeforeDelete = () => Assert.NotEmpty(fixture.LegacyValues);
            await operation.ClearHostDataAsync(CancellationToken.None);
            Assert.Null(store.Value);
            Assert.Empty(fixture.LegacyValues);
            Assert.Equal(23456, fixture.Containers[0].Session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
            Assert.True(Directory.Exists(manifest.Descriptor.RootPath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ClearLocalFilesAsync(CancellationToken.None));
            Assert.True(Directory.Exists(manifest.Descriptor.RootPath));

            await fixture.Manager.DisposeAsync();
            Assert.True(fixture.Manager.IsDisposalComplete);
            await operation.ClearLocalFilesAsync(CancellationToken.None);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory.RootPath));
            Assert.Equal("outside owned data", await File.ReadAllTextAsync(outside));
            Assert.Throws<ObjectDisposedException>(() => logs.AppendLog("Info", "Late", "must not recreate", null));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory.RootPath));
            await using Fixture reopened = new(directory);
            _ = await reopened.StartAsync();
            Assert.Equal(10000, reopened.Containers[0].Session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task GenerationRemoval_CredentialFailurePreservesMigrationInputAndEveryDataFile()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        var manifest = await fixture.StartAsync();
        var settings = CreateRemovalSettings(fixture);
        RemovalCredentialStore store = new() { RetainOnDelete = true };
        using var credentials = CreateRemovalCredentials(fixture, store);
        var operation = CreateRemovalFactory(fixture, directory.RootPath, settings, credentials, new(fixture.Admission)).Create();

        await operation.PrepareShutdownAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ControllerCredentialException>(() => operation.ClearHostDataAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ClearLocalFilesAsync(CancellationToken.None));

        Assert.NotNull(store.Value);
        Assert.Equal(23456, fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value]);
        Assert.True(Directory.Exists(manifest.Descriptor.RootPath));
        var remaining = await directory.Store.LoadCurrentAsync(CancellationToken.None);
        Assert.NotNull(remaining);
        Assert.Equal(manifest.ContentHash, remaining.ContentHash);
        Assert.True(manifest.Descriptor.IsSameGeneration(remaining.Descriptor));
    }

    [Fact]
    public async Task GenerationRemoval_RefusesAnotherApplicationRootBeforeShutdownOrCredentialDeletion()
    {
        await using DataGenerationTestDirectory directory = new();
        await using DataGenerationTestDirectory wrong = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        var settings = CreateRemovalSettings(fixture);
        RemovalCredentialStore store = new();
        using var credentials = CreateRemovalCredentials(fixture, store);
        RemovalNetwork network = new(fixture.Admission);

        Assert.Throws<DataGenerationStoreException>(() => CreateRemovalFactory(fixture, wrong.RootPath, settings, credentials, network).Create());

        Assert.Equal(0, network.Calls);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.False(Directory.Exists(wrong.RootPath));
    }

    [Fact]
    public async Task GenerationRemoval_FailedOwnerDisposalCannotPermitPartialFileDeletion()
    {
        await using DataGenerationTestDirectory directory = new();
        Producer producer = new() { FailStop = true };
        await using Fixture fixture = new(directory, producer: producer);
        var manifest = await fixture.StartAsync();
        var settings = CreateRemovalSettings(fixture);
        using var credentials = CreateRemovalCredentials(fixture, new());
        var operation = CreateRemovalFactory(fixture, directory.RootPath, settings, credentials, new(fixture.Admission)).Create();
        await operation.PrepareShutdownAsync(CancellationToken.None);
        await operation.ClearHostDataAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Manager.DisposeAsync().AsTask());
            Assert.False(fixture.Manager.IsDisposalComplete);
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ClearLocalFilesAsync(CancellationToken.None));
            Assert.True(Directory.Exists(manifest.Descriptor.RootPath));
        }
        finally { producer.FailStop = false; }
        await fixture.Manager.DisposeAsync();
        Assert.True(fixture.Manager.IsDisposalComplete);
        await operation.ClearLocalFilesAsync(CancellationToken.None);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.RootPath));
    }

    [Fact]
    public async Task GenerationRemoval_LegacyErasureRequiresTheMatchingTerminalLeaseAndDoesNotPublishSettings()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        _ = await fixture.StartAsync();
        var settings = CreateRemovalSettings(fixture);
        settings.SettingChanged += (_, _) => throw new InvalidOperationException("A stopped consumer must not be notified.");
        using (var ordinary = fixture.Admission.AcquireOrdinary())
        {
            Assert.Throws<InvalidOperationException>(() => settings.ClearLegacySettingsForRemoval(ordinary));
        }
        await using (var destructive = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None))
        {
            Assert.Throws<InvalidOperationException>(() => settings.ClearLegacySettingsForRemoval(destructive));
        }
        MutationAdmissionBarrier foreign = new();
        await using (var wrong = await foreign.CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, CancellationToken.None))
        {
            Assert.Throws<InvalidOperationException>(() => settings.ClearLegacySettingsForRemoval(wrong));
        }
        Assert.NotEmpty(fixture.LegacyValues);
        await using (var terminal = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, CancellationToken.None)) { }
        using (var maintenance = fixture.Admission.AcquireShutdownMaintenance()) { settings.ClearLegacySettingsForRemoval(maintenance); }
        Assert.Empty(fixture.LegacyValues);
        Assert.Equal(23456, settings.MixedPort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationRemoval_VerifiesLegacyErasureWhenTheStorageReplyFails(bool retainValues)
    {
        MutationAdmissionBarrier admission = new();
        ClearReplyDictionary values = new(retainValues) { ["obsolete-setting"] = "private data" };
        UiService.AppSettingsService settings = new(values);
        settings.ConfigureMutationAdmission(admission);
        await using (var terminal = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, CancellationToken.None)) { }
        using var lease = admission.AcquireShutdownMaintenance();
        if (retainValues) { Assert.Throws<InvalidOperationException>(() => settings.ClearLegacySettingsForRemoval(lease)); }
        else { settings.ClearLegacySettingsForRemoval(lease); }
        Assert.Equal(retainValues ? 1 : 0, values.Count);
    }

    private static UiService.AppSettingsService CreateRemovalSettings(Fixture fixture)
    {
        // Nullable annotations do not change the dictionary's CLR identity; share the exact migration map.
        UiService.AppSettingsService settings = new((IDictionary<string, object>)(object)fixture.LegacyValues);
        settings.ConfigureMutationAdmission(fixture.Admission);
        settings.BindAuthority(fixture.Authority);
        return settings;
    }

    private static ControllerCredentialService CreateRemovalCredentials(Fixture fixture, RemovalCredentialStore store)
    {
        ControllerCredentialService credentials = new(store, fixture.Admission);
        using var lease = fixture.Admission.AcquireOrdinary();
        credentials.InitializeAdmitted(lease, CancellationToken.None);
        return credentials;
    }

    private static UiData.GenerationDataClearOperationFactory CreateRemovalFactory(Fixture fixture, string root,
        UiService.AppSettingsService settings, ControllerCredentialService credentials, RemovalNetwork network) =>
        new(fixture.Manager, fixture.Admission, credentials, settings,
            new RuntimeLifecycleCoordinator(fixture.Admission, network,
                () => NetworkIntent.Shutdown(ClashSharpMode.RuleTakeover, true, 10000), []), root);

    private sealed class RemovalNetwork(MutationAdmissionBarrier admission) : IRuntimeShutdownNetworkCoordinator
    {
        public int Calls { get; private set; }
        public NetworkIntent? LastIntent { get; private set; }
        public Task<MutationResult<NetworkTransitionResult>> ApplyShutdownAsync(NetworkIntent intent, MutationAdmissionLease lease, CancellationToken token)
        {
            admission.EnsureActiveExclusiveLease(lease);
            Calls++;
            LastIntent = intent;
            return Task.FromResult(new MutationResult<NetworkTransitionResult>(Guid.NewGuid(), MutationOutcome.Succeeded,
                new(ClashSharpMode.Disabled, false, false, false, intent.MixedPort, "disabled"), null));
        }
    }

    private sealed class RemovalCredentialStore : IControllerCredentialStore
    {
        public string? Value { get; private set; } = new('a', 64);
        public bool RetainOnDelete { get; init; }
        public int Deletes { get; private set; }
        public Action? BeforeDelete { get; set; }
        public bool TryRead(out string? secret) { secret = Value; return Value is not null; }
        public void Write(string secret) => Value = secret;
        public void Delete() { BeforeDelete?.Invoke(); Deletes++; if (!RetainOnDelete) { Value = null; } }
    }

    private sealed class ClearReplyDictionary(bool retainValues) : Dictionary<string, object>, IDictionary<string, object>
    {
        void ICollection<KeyValuePair<string, object>>.Clear()
        {
            if (!retainValues) { base.Clear(); }
            throw new IOException("storage reply unavailable");
        }
    }
}

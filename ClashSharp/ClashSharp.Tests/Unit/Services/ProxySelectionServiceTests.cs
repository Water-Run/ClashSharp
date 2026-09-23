using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies durable choices across owners, profiles, cancellation, and persistence failures.</summary>
public sealed class ProxySelectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClashSharp-ProxySelections", Guid.NewGuid().ToString("N"));

    /// <summary>Verifies a fresh store and a restarted controller recover choices only for their profile.</summary>
    [Fact]
    public async Task SelectAndRestore_PersistsAcrossInstancesAndSeparatesProfiles()
    {
        FakeController controller = new();
        string path = Path.Combine(_root, "selections.json");
        ProxySelectionStore store = new(path);
        ProxySelectionService service = Create(store, controller);
        await service.SelectAsync("GLOBAL", "Proxy B", CancellationToken.None);
        Assert.Equal("Proxy B", new ProxySelectionStore(path).Read("profile-a")["GLOBAL"]);

        // The other profile can inherit a same-named cache entry from the old owner's core.
        controller.Selection = "Proxy B";
        ProxySelectionService restarted = Create(new ProxySelectionStore(path), controller);
        await restarted.RestoreAsync(Plan("profile-b"), CancellationToken.None);
        Assert.Equal("DIRECT", controller.Selection);
        await restarted.RestoreAsync(Plan("profile-a"), CancellationToken.None);
        Assert.Equal("Proxy B", controller.Selection);
    }

    /// <summary>Verifies a removed subscription candidate is never sent back to the controller.</summary>
    [Fact]
    public async Task Restore_RemovedCandidateKeepsCurrentValidChoice()
    {
        ProxySelectionStore store = new(Path.Combine(_root, "selections.json"));
        store.Save("profile-a", "GLOBAL", "Removed node");
        FakeController controller = new();
        await Create(store, controller).RestoreAsync(Plan("profile-a"), CancellationToken.None);
        Assert.Empty(controller.Writes);
        Assert.Equal("DIRECT", controller.Selection);
    }

    /// <summary>Verifies a failed durable write restores the live selection and does not claim success.</summary>
    [Fact]
    public async Task Select_WhenStoreFails_RestoresAndVerifiesPreviousChoice()
    {
        FakeController controller = new();
        ProxySelectionService service = Create(new FailingStore(), controller);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SelectAsync("GLOBAL", "Proxy B", CancellationToken.None));
        Assert.Equal(["Proxy B", "DIRECT"], controller.Writes);
        Assert.Equal("DIRECT", controller.Selection);
    }

    /// <summary>Verifies caller cancellation after dispatch cannot strand an unpersisted selection.</summary>
    [Fact]
    public async Task Select_WhenCancelledAfterDispatch_RestoresWithoutSaving()
    {
        using CancellationTokenSource cancellation = new();
        FakeController controller = new() { AfterSelection = selected => { if (selected == "Proxy B") { cancellation.Cancel(); } } };
        ProxySelectionStore store = new(Path.Combine(_root, "selections.json"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(store, controller).SelectAsync("GLOBAL", "Proxy B", cancellation.Token));
        Assert.Equal("DIRECT", controller.Selection);
        Assert.Empty(store.Read("profile-a"));
    }

    /// <summary>Verifies stale UI candidates are rejected before either durable or live state changes.</summary>
    [Fact]
    public async Task Select_InvalidCandidateHasNoSideEffects()
    {
        FakeController controller = new();
        ProxySelectionStore store = new(Path.Combine(_root, "selections.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(store, controller).SelectAsync("GLOBAL", "Missing node", CancellationToken.None));
        Assert.Empty(controller.Writes);
        Assert.Empty(store.Read("profile-a"));
    }

    /// <summary>Verifies an acknowledgement without the selected runtime value is not persisted.</summary>
    [Fact]
    public async Task Select_ControllerIgnoresChoice_IsRejected()
    {
        FakeController controller = new() { IgnoreSelection = true };
        ProxySelectionStore store = new(Path.Combine(_root, "selections.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(store, controller).SelectAsync("GLOBAL", "Proxy B", CancellationToken.None));
        Assert.Empty(store.Read("profile-a"));
    }

    /// <summary>Verifies a stale request cannot select a node in a newly activated profile.</summary>
    [Fact]
    public async Task Select_WhenRuntimeChangesBeforeDispatch_DoesNotMutate()
    {
        int observations = 0;
        FakeController controller = new();
        ProxySelectionStore store = new(Path.Combine(_root, "selections.json"));
        ProxySelectionService service = new(store, controller.ReadAsync, controller.SelectAsync,
            () => new(true, Plan(++observations == 1 ? "profile-a" : "profile-b"), 1, new string('a', 64)),
            UncoordinatedProfileCatalogMutationCoordinator.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectAsync("GLOBAL", "Proxy B", CancellationToken.None));
        Assert.Empty(controller.Writes);
        Assert.Empty(store.Read("profile-a"));
        Assert.Empty(store.Read("profile-b"));
    }

    /// <summary>Verifies rollback never overwrites a different owner's runtime after dispatch.</summary>
    [Fact]
    public async Task Select_WhenRuntimeChangesAfterDispatch_DoesNotPersistOrRollbackNewOwner()
    {
        bool switched = false;
        FakeController controller = new() { AfterSelection = _ => switched = true };
        ProxySelectionStore store = new(Path.Combine(_root, "selections.json"));
        ProxySelectionService service = new(store, controller.ReadAsync, controller.SelectAsync,
            () => new(true, Plan(switched ? "profile-b" : "profile-a"), switched ? 2 : 1, new string('a', 64)),
            UncoordinatedProfileCatalogMutationCoordinator.Instance);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SelectAsync("GLOBAL", "Proxy B", CancellationToken.None));
        Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(["Proxy B"], controller.Writes);
        Assert.Empty(store.Read("profile-a"));
        Assert.Empty(store.Read("profile-b"));
    }

    /// <summary>Verifies an unacknowledged restore blocks activation instead of reporting success.</summary>
    [Fact]
    public async Task Restore_WhenControllerIgnoresSavedChoice_Fails()
    {
        ProxySelectionStore store = new(Path.Combine(_root, "selections.json"));
        store.Save("profile-a", "GLOBAL", "Proxy B");
        FakeController controller = new() { IgnoreSelection = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(store, controller).RestoreAsync(Plan("profile-a"), CancellationToken.None));
        Assert.Equal("DIRECT", controller.Selection);
        Assert.Equal("Proxy B", store.Read("profile-a")["GLOBAL"]);
    }

    /// <summary>Verifies shutdown admission prevents new node mutations.</summary>
    [Fact]
    public async Task Select_WhenAdmissionClosed_DoesNotContactController()
    {
        MutationAdmissionBarrier admission = new();
        FairAsyncMutationGate gate = new();
        await using MutationAdmissionLease closure = await admission.CloseAndDrainAsync(
            MutationAdmissionClosure.Shutdown, CancellationToken.None);
        FakeController controller = new();
        ProxySelectionStore store = new(Path.Combine(_root, "selections.json"));
        ProxySelectionService service = new(store, controller.ReadAsync, controller.SelectAsync,
            () => new(true, Plan("profile-a"), 1, new string('a', 64)),
            new ProfileCatalogMutationCoordinator(admission, gate));

        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(() => service.SelectAsync("GLOBAL", "Proxy B", CancellationToken.None));
        Assert.Empty(controller.Writes);
        Assert.Equal(0, controller.Reads);
    }

    /// <summary>Verifies import/reset replacement is observed without restarting the store.</summary>
    [Fact]
    public void Store_ReadsReplacedDataAndKeepsOtherProfiles()
    {
        string path = Path.Combine(_root, "selections.json");
        ProxySelectionStore store = new(path);
        store.Save("profile-a", "GLOBAL", "Proxy B");
        store.Save("profile-b", "GLOBAL", "DIRECT");
        Assert.Equal("Proxy B", store.Read("profile-a")["GLOBAL"]);
        Assert.Equal("DIRECT", store.Read("profile-b")["GLOBAL"]);
        File.WriteAllText(path, "{\"version\":1,\"profiles\":{}}");
        Assert.Empty(store.Read("profile-a"));
    }

    /// <summary>Verifies corrupt backups are rejected without silently resetting or overwriting saved choices.</summary>
    [Theory]
    [InlineData("{\"version\":2,\"profiles\":{}}")]
    [InlineData("{\"version\":1,\"profiles\":{\"p\":{},\"p\":{}}}")]
    [InlineData("{\"version\":1,\"profiles\":{\"p\":{\"GLOBAL\":\"A\",\"GLOBAL\":\"B\"}}}")]
    [InlineData("{\"version\":1,\"profiles\":{\"p\":{\"GLOBAL\":null}}}")]
    public void Store_InvalidDocumentDoesNotOverwrite(string json)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "selections.json");
        File.WriteAllText(path, json);
        ProxySelectionStore store = new(path);
        Assert.Throws<InvalidDataException>(() => store.Save("profile-a", "GLOBAL", "B"));
        Assert.Equal(json, File.ReadAllText(path));
    }

    private static ProxySelectionService Create(IProxySelectionStore store, FakeController controller) =>
        new(store, controller.ReadAsync, controller.SelectAsync,
            () => new(true, Plan("profile-a"), 1, new string('a', 64)),
            UncoordinatedProfileCatalogMutationCoordinator.Instance);

    private static RuntimeConfigurationActivationPlan Plan(string profile) => new(ClashSharpMode.FullTakeover, false, 10000, profile);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private sealed class FailingStore : IProxySelectionStore
    {
        public IReadOnlyDictionary<string, string> Read(string profileId) => new Dictionary<string, string>();

        public void Save(string profileId, string groupName, string proxyName) => throw new IOException("Disk unavailable.");
    }

    private sealed class FakeController
    {
        public string Selection { get; set; } = "DIRECT";
        public List<string> Writes { get; } = [];
        public int Reads { get; private set; }
        public bool IgnoreSelection { get; init; }
        public Action<string>? AfterSelection { get; init; }

        public Task<IReadOnlyList<MihomoProxyGroup>> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult<IReadOnlyList<MihomoProxyGroup>>(
                [new("GLOBAL", "Selector", Selection, ["DIRECT", "Proxy B"])]);
        }

        public Task SelectAsync(string groupName, string proxyName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("GLOBAL", groupName);
            Writes.Add(proxyName);
            if (!IgnoreSelection) { Selection = proxyName; }
            AfterSelection?.Invoke(proxyName);
            return Task.CompletedTask;
        }
    }
}

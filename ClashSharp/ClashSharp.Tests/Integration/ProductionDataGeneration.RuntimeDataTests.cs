extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
using Binding = ClashSharpUi::ClashSharp.Hosting.Data.RuntimeDataBinding;
using ConfigFacade = ClashSharpUi::ClashSharp.Hosting.Data.GenerationCoreConfigurationStore;
using LogFacade = ClashSharpUi::ClashSharp.Hosting.Data.GenerationLogStorage;
using Mode = ClashSharp.Model.ClashSharpMode;
using MutationCoordinator = ClashSharpUi::ClashSharp.Service.ProfileCatalogMutationCoordinator;
using Plan = ClashSharpUi::ClashSharp.Service.RuntimeConfigurationActivationPlan;
using Ports = ClashSharpUi::ClashSharp.Service.RuntimeDataServices;
using ProxyGroup = ClashSharpUi::ClashSharp.Model.MihomoProxyGroup;
using SelectionFacade = ClashSharpUi::ClashSharp.Hosting.Data.GenerationProxySelectionService;
using SelectionStore = ClashSharpUi::ClashSharp.Service.ProxySelectionStore;

namespace ClashSharp.Tests.Integration;

[CollectionDefinition("Runtime data ownership", DisableParallelization = true)]
public sealed class RuntimeDataOwnership;

[Collection("Runtime data ownership")]
public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task FirstGeneration_RestoresLegacyNodeChoiceAndCommitsLaterChoicesOnlyToNewRoot()
    {
        await using DataGenerationTestDirectory directory = new();
        string legacyPath = Path.Combine(directory.RootPath, "mihomo", "proxy-selections.json");
        SelectionStore legacy = new(legacyPath);
        legacy.Save("profile-a", "GLOBAL", "Node B");
        byte[] original = await File.ReadAllBytesAsync(legacyPath);
        await using Fixture fixture = new(directory);
        SelectionController controller = ConfigureSelections(fixture);
        DataGenerationManifestSnapshot manifest = await fixture.StartAsync();
        using Binding binding = new(fixture.Manager);

        await Ports.ProxySelections.RestoreAsync(SelectionPlan, CancellationToken.None);

        Assert.Equal("Node B", controller.Selection);
        await Ports.ProxySelections.SelectAsync("GLOBAL", "Node C", CancellationToken.None);
        Assert.Equal("Node C", new SelectionStore(Path.Combine(manifest.Descriptor.RootPath, "mihomo", "proxy-selections.json")).Read("profile-a")["GLOBAL"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(legacyPath));
    }

    [Fact]
    public async Task NativeFactoryPorts_FollowConfigurationLogsAndSavedChoicesAcrossGenerationReplacement()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        SelectionController controller = ConfigureSelections(fixture);
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        using Binding binding = new(fixture.Manager);
        var configuration = Ports.Configuration;
        var logs = Ports.Logs;
        var selections = Ports.ProxySelections;
        const string profile = "routing-profile";
        const string original = "proxies: []\nproxy-groups: []\nrules:\n  - MATCH,DIRECT\n";
        var imported = await configuration.ImportProfileConfigurationAsync(profile, "original", original, CancellationToken.None);
        Assert.True(configuration.TryReadProfileConfigurationText(profile, out string? firstText));
        Assert.Contains("MATCH,DIRECT", firstText, StringComparison.Ordinal);
        logs.AppendLog("Info", "Routing", "original", null);
        await selections.SelectAsync("GLOBAL", "Node B", CancellationToken.None);
        string oldSelections = Path.Combine(baseline.Descriptor.RootPath, "mihomo", "proxy-selections.json");
        byte[] saved = await File.ReadAllBytesAsync(oldSelections);

        await fixture.ReplaceWithEmptyGenerationAsync();

        Assert.Same(configuration, Ports.Configuration);
        Assert.Same(logs, Ports.Logs);
        Assert.Same(selections, Ports.ProxySelections);
        Assert.False(configuration.TryReadProfileConfigurationText(profile, out _));
        Assert.Empty(logs.GetRecentLogs(10));
        string currentRoot = fixture.Manager.CurrentManifest.Descriptor.RootPath;
        Assert.StartsWith(currentRoot, configuration.GetState().DirectoryPath, StringComparison.OrdinalIgnoreCase);
        // A new root has no saved choice; activation must not pick up the old root's Node B.
        await selections.RestoreAsync(SelectionPlan, CancellationToken.None);
        Assert.Equal("DIRECT", controller.Selection);
        await selections.SelectAsync("GLOBAL", "Node C", CancellationToken.None);
        logs.AppendLog("Info", "Routing", "replacement", null);
        await configuration.ImportProfileConfigurationAsync(profile, "replacement", "proxies: []\nproxy-groups: []\nrules:\n  - MATCH,REJECT\n", CancellationToken.None);
        Assert.True(configuration.TryReadProfileConfigurationText(profile, out string? nextText));
        Assert.Contains("MATCH,REJECT", nextText, StringComparison.Ordinal);
        Assert.Equal("replacement", Assert.Single(logs.GetRecentLogs(10)).Message);
        Assert.Equal("Node C", new SelectionStore(Path.Combine(currentRoot, "mihomo", "proxy-selections.json")).Read("profile-a")["GLOBAL"]);
        Assert.Equal(saved, await File.ReadAllBytesAsync(oldSelections));
        Assert.Contains("MATCH,DIRECT", await File.ReadAllTextAsync(imported.ConfigPath), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory.RootPath, "ClashSharpLogs.sqlite3")));
        Assert.False(File.Exists(Path.Combine(directory.RootPath, "mihomo", "proxy-selections.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationImport_HoldsGenerationThroughValidationAndReleasesItOnFailure(bool failValidation)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        ConfigFacade configuration = new(fixture.Manager);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Validator.OnValidate = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            if (failValidation) { throw new IOException("validation failed"); }
        };
        var import = configuration.ImportProfileConfigurationAsync("owned", "owned", "proxies: []\nproxy-groups: []\nrules:\n  - MATCH,DIRECT\n", CancellationToken.None);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = fixture.Manager.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.Throws<DataGenerationManagerException>(() => configuration.GetState());
            Assert.All(fixture.Participants, participant => Assert.Equal(0, participant.Disposals));
        }
        finally { release.TrySetResult(); }
        if (failValidation) { await Assert.ThrowsAsync<IOException>(() => import); }
        else { Assert.True(File.Exists((await import).ConfigPath)); }
        await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(fixture.Participants, participant => Assert.Equal(1, participant.Disposals));
    }

    [Fact]
    public async Task NodeChoice_HoldsGenerationUntilAcknowledgementAndDurableSaveComplete()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        SelectionController controller = ConfigureSelections(fixture);
        DataGenerationManifestSnapshot manifest = await fixture.StartAsync();
        SelectionFacade selections = new(() => fixture.Manager);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.BeforeSelection = async () => { entered.TrySetResult(); await release.Task; };
        Task selecting = selections.SelectAsync("GLOBAL", "Node B", CancellationToken.None);
        Task? retiring = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retiring = fixture.Manager.DisposeAsync().AsTask();
            Assert.False(retiring.IsCompleted);
            Assert.False(selecting.IsCompleted);
            Assert.All(fixture.Participants, participant => Assert.Equal(0, participant.Disposals));
        }
        finally { release.TrySetResult(); }
        await selecting.WaitAsync(TimeSpan.FromSeconds(5));
        await retiring!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Node B", new SelectionStore(Path.Combine(manifest.Descriptor.RootPath, "mihomo", "proxy-selections.json")).Read("profile-a")["GLOBAL"]);
        await Assert.ThrowsAsync<DataGenerationManagerException>(() => selections.RestoreAsync(SelectionPlan, CancellationToken.None));
    }

    [Fact]
    public async Task RecoveryFallback_IsLimitedToUninitializedManagerAndCannotReviveAfterRetirement()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = ConfigureSelections(fixture);
        int legacyReads = 0;
        ConfigFacade configuration = new(() => fixture.Manager, () => { ++legacyReads; throw new IOException("legacy configuration"); });
        LogFacade logs = new(() => fixture.Manager, () => { ++legacyReads; throw new IOException("legacy logs"); });
        SelectionFacade selections = new(() => fixture.Manager, () => { ++legacyReads; throw new IOException("legacy selections"); });
        Assert.Throws<IOException>(() => configuration.GetState());
        Assert.Throws<IOException>(() => logs.GetRecentLogs(1));
        await Assert.ThrowsAsync<IOException>(() => selections.RestoreAsync(SelectionPlan, CancellationToken.None));
        Assert.Equal(3, legacyReads);
        _ = await fixture.StartAsync();
        _ = configuration.GetState();
        Assert.Empty(logs.GetRecentLogs(1));
        await selections.RestoreAsync(SelectionPlan, CancellationToken.None);
        Assert.Equal(3, legacyReads);
        await fixture.Manager.DisposeAsync();
        Assert.Throws<DataGenerationManagerException>(() => configuration.GetState());
        Assert.Throws<DataGenerationManagerException>(() => logs.GetRecentLogs(1));
        await Assert.ThrowsAsync<DataGenerationManagerException>(() => selections.RestoreAsync(SelectionPlan, CancellationToken.None));
        Assert.Equal(3, legacyReads);
    }

    [Fact]
    public async Task HostBinding_RejectsOverlappingOwnersAndOldDisposalCannotDisconnectReplacement()
    {
        Assert.Throws<InvalidOperationException>(() => Ports.Logs.GetRecentLogs(1));
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        using Binding initial = new(fixture.Manager);
        Assert.Throws<InvalidOperationException>(() => new Binding(fixture.Manager));
        Assert.Empty(Ports.Logs.GetRecentLogs(1));
        initial.Dispose();
        Assert.Throws<InvalidOperationException>(() => Ports.Configuration.GetState());
        using Binding replacement = new(fixture.Manager);
        initial.Dispose();
        Ports.Logs.AppendLog("Info", "Binding", "replacement owner", null);
        Assert.Equal("replacement owner", Assert.Single(Ports.Logs.GetRecentLogs(1)).Message);
        await fixture.Manager.DisposeAsync();
        Assert.Throws<DataGenerationManagerException>(() => Ports.Logs.GetRecentLogs(1));
        replacement.Dispose();
        Assert.Throws<InvalidOperationException>(() => Ports.Logs.GetRecentLogs(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ports.FlushSamplingAsync(CancellationToken.None));
    }

    private static Plan SelectionPlan => new(Mode.FullTakeover, false, 10000, "profile-a");

    private static SelectionController ConfigureSelections(Fixture fixture)
    {
        SelectionController controller = new();
        fixture.CreateProxySelections = configuration => new(
            new SelectionStore(Path.Combine(configuration.GetState().DirectoryPath, "proxy-selections.json")),
            controller.ReadAsync, controller.SelectAsync,
            () => new(true, SelectionPlan, 1, new string('a', 64)),
            new MutationCoordinator(fixture.Admission, fixture.MutationGate));
        return controller;
    }

    private sealed class SelectionController
    {
        public string Selection { get; private set; } = "DIRECT";
        public Func<Task>? BeforeSelection { get; set; }
        public Task<IReadOnlyList<ProxyGroup>> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ProxyGroup>>([new("GLOBAL", "Selector", Selection, ["DIRECT", "Node B", "Node C"])]);
        }
        public async Task SelectAsync(string group, string node, CancellationToken token)
        {
            Assert.Equal("GLOBAL", group);
            if (BeforeSelection is not null) { await BeforeSelection(); }
            token.ThrowIfCancellationRequested();
            Selection = node;
        }
    }
}

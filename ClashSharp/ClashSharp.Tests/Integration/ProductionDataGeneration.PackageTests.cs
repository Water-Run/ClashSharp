extern alias ClashSharpUi;
using System.Text;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using ClashSharp.Settings;
using Exporter = ClashSharpUi::ClashSharp.Hosting.Data.GenerationDataPackageExporter;
using PackageReader = ClashSharpUi::ClashSharp.Service.ClashDataPackageService;
using PackageSettings = ClashSharpUi::ClashSharp.Service.DataPackageSettingsSnapshot;
using Preparer = ClashSharpUi::ClashSharp.Hosting.Data.GenerationDataCandidatePreparer;
using Sampling = ClashSharpUi::ClashSharp.Hosting.Data.GenerationSamplingRuntime;
using UiSettings = ClashSharpUi::ClashSharp.Service.AppSettingsService;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task ReplacementSettings_DoNotReuseVerifiedNativeEvidenceOrPendingIdentities()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new Sampling(fixture.Manager), new UiSettings(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        SettingsEnvelope baseline = fixture.Containers[0].Session.Snapshot;
        Assert.DoesNotContain(baseline.Applied.Values, state => state.Kind == SettingAppliedStateKind.Unknown);
        SettingsGenerationPlanner planner = new(SettingsRegistry.Default);
        Guid candidateId = Guid.NewGuid();
        SettingDefinition port = SettingsRegistry.Default.Get(SettingsRegistry.Keys.MixedPort.Value);
        SettingValueChange[] changes = [new(port.Key, port.Normalize("23456").Value!)];

        SettingsEnvelope candidate = planner.Create(baseline, changes, candidateId);

        Assert.Equal(1, candidate.EnvelopeRevision);
        Assert.Equal(23456, candidate.Desired[port.Key].Value.Get<int>());
        Assert.NotEqual(candidate.Desired[port.Key].Value, baseline.Desired[port.Key].Value);
        Assert.All(candidate.Applied.Values, state => Assert.Equal(SettingAppliedStateKind.Unknown, state.Kind));
        Assert.All(candidate.PendingApplications, batch => Assert.Equal(SettingsApplicationBatchState.Pending, batch.State));
        Assert.Equal(baseline.MigrationHistory, candidate.MigrationHistory);
        Assert.Equal(candidate.PendingApplications.Select(batch => batch.BatchId), planner.Create(baseline, changes, candidateId).PendingApplications.Select(batch => batch.BatchId));
        Assert.Empty(candidate.PendingApplications.Select(batch => batch.BatchId).Intersect(planner.Create(baseline, changes, Guid.NewGuid()).PendingApplications.Select(batch => batch.BatchId)));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => planner.Create(baseline, [changes[0], changes[0]], candidateId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidatePreparation_RejectsForeignOwnershipOrCancellationBeforeReadingPackage(bool cancelled)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        await using MutationAdmissionLease own = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        await using MutationAdmissionLease foreign = await new MutationAdmissionBarrier().CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        Func<Task> prepare = () => fixture.CreateCandidatePreparer().PrepareImportAdmittedAsync(
            transition, Path.Combine(directory.RootPath, "absent-package.xml"), cancelled ? own : foreign, new CancellationToken(cancelled));

        if (cancelled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(prepare); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(prepare); }

        Assert.Single(Directory.GetDirectories(directory.Policy.GenerationsRootPath));
        Assert.Single(fixture.Containers);
        Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    [Fact]
    public async Task Export_CapturesCurrentSettingsAndProfilesAfterReplacementWithoutReadingLegacyRoot()
    {
        await using DataGenerationTestDirectory directory = new();
        await using DataGenerationTestDirectory backups = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 12345;
        _ = await fixture.StartAsync();
        Exporter exporter = new(fixture.Manager, fixture.Admission, directory.RootPath);
        string first = Path.Combine(backups.RootPath, "first.xml");
        await new SettingsExportCoordinator(fixture.Admission).ExecuteAsync((lease, token) =>
            exporter.ExportAdmittedAsync(first, ClashDataPackageScope.Settings, lease, token), CancellationToken.None);
        Assert.Equal("12345", PackageSetting(XDocument.Load(first), SettingsRegistry.Keys.MixedPort));
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        await fixture.ReplaceWithEmptyGenerationAsync();
        var imported = await fixture.Containers[^1].Profiles.ImportLocalProfileAsync(await WriteProfileInputAsync(directory), CancellationToken.None);
        string currentRoot = fixture.Manager.CurrentManifest.Descriptor.RootPath;
        await File.WriteAllTextAsync(Path.Combine(currentRoot, "mihomo", "config.yaml"), "generated runtime with private credentials");
        await File.WriteAllTextAsync(Path.Combine(directory.RootPath, "ProfileCatalog.json"), "obsolete legacy catalog");
        string second = Path.Combine(backups.RootPath, "second.xml");

        await new SettingsExportCoordinator(fixture.Admission).ExecuteAsync((lease, token) =>
            exporter.ExportAdmittedAsync(second, ClashDataPackageScope.SettingsAndProxyConfiguration, lease, token), CancellationToken.None);

        XDocument package = XDocument.Load(second);
        Assert.Equal("23456", PackageSetting(package, SettingsRegistry.Keys.MixedPort));
        var plan = new PackageReader(new PackageSettings(fixture.Containers[^1].Session.Snapshot), currentRoot)
            .ReadImportPlan(second, CancellationToken.None);
        Assert.Contains(plan.Files, file => file.RelativePath.Replace('\\', '/') == $"mihomo/profiles/{imported.ProfileId}/config.yaml");
        Assert.DoesNotContain(plan.Files, file => file.RelativePath.Replace('\\', '/') == "mihomo/config.yaml");
        Assert.DoesNotContain("obsolete legacy catalog", Encoding.UTF8.GetString(plan.Files.Single(file => file.RelativePath == "ProfileCatalog.json").Content.Span), StringComparison.Ordinal);
        Assert.Equal("12345", PackageSetting(XDocument.Load(first), SettingsRegistry.Keys.MixedPort));
    }

    [Fact]
    public async Task Export_RequiresExclusiveOwnershipAndRejectsOverwritingManagedData()
    {
        await using DataGenerationTestDirectory directory = new();
        await using DataGenerationTestDirectory backups = new();
        await using Fixture fixture = new(directory);
        DataGenerationManifestSnapshot manifest = await fixture.StartAsync();
        Exporter exporter = new(fixture.Manager, fixture.Admission, directory.RootPath);
        string backup = Path.Combine(backups.RootPath, "backup.xml");
        await using (MutationAdmissionLease ordinary = await fixture.Admission.AcquireOrdinaryAsync(CancellationToken.None))
        {
            Assert.NotNull(await Record.ExceptionAsync(() => exporter.ExportAdmittedAsync(backup, ClashDataPackageScope.Settings, ordinary, CancellationToken.None)));
        }
        MutationAdmissionBarrier other = new();
        await using (MutationAdmissionLease foreign = await other.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None))
        {
            Assert.NotNull(await Record.ExceptionAsync(() => exporter.ExportAdmittedAsync(backup, ClashDataPackageScope.Settings, foreign, CancellationToken.None)));
        }
        Assert.False(Directory.Exists(backups.RootPath));
        string catalog = Path.Combine(manifest.Descriptor.RootPath, "ProfileCatalog.json");
        byte[] before = await File.ReadAllBytesAsync(catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SettingsExportCoordinator(fixture.Admission).ExecuteAsync((lease, token) =>
            exporter.ExportAdmittedAsync(catalog, ClashDataPackageScope.Settings, lease, token), CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(catalog));
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task ImportPreparation_OpensCompleteUnpublishedRepositoriesAndPreservesSourceBytes()
    {
        await using DataGenerationTestDirectory directory = new();
        await using DataGenerationTestDirectory backups = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 12345;
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        var repositories = fixture.Containers[0];
        var imported = await repositories.Profiles.ImportLocalProfileAsync(await WriteProfileInputAsync(directory), CancellationToken.None);
        repositories.Logs.AppendLog("Info", "Import", "retained log", null);
        string selectionPath = Path.Combine(baseline.Descriptor.RootPath, "mihomo", "proxy-selections.json");
        new ClashSharpUi::ClashSharp.Service.ProxySelectionStore(selectionPath).Save(imported.ProfileId, "GLOBAL", "Node B");
        await File.WriteAllTextAsync(Path.Combine(baseline.Descriptor.RootPath, "mihomo", "config.yaml"), "private generated runtime");
        string backup = Path.Combine(backups.RootPath, "import.xml");
        Exporter exporter = new(fixture.Manager, fixture.Admission, directory.RootPath);
        await new SettingsExportCoordinator(fixture.Admission).ExecuteAsync((lease, token) =>
            exporter.ExportAdmittedAsync(backup, ClashDataPackageScope.SettingsAndProxyConfiguration, lease, token), CancellationToken.None);
        XDocument package = XDocument.Load(backup);
        PackageSettingElement(package, SettingsRegistry.Keys.MixedPort).SetAttributeValue("Value", "23456");
        XElement profile = package.Root!.Element("Files")!.Elements("File").Single(file =>
            ((string)file.Attribute("Path")!).Replace('\\', '/') == $"mihomo/profiles/{imported.ProfileId}/config.yaml");
        profile.Value = Convert.ToBase64String(Encoding.UTF8.GetBytes("proxies: []\nproxy-groups: []\nrules:\n  - MATCH,REJECT\n"));
        await File.WriteAllTextAsync(backup, package.ToString(SaveOptions.DisableFormatting));
        byte[] sourceProfile = await File.ReadAllBytesAsync(imported.ConfigPath);
        byte[] sourceSelections = await File.ReadAllBytesAsync(selectionPath);
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);

        await using DataGenerationScope candidate = await fixture.CreateCandidatePreparer().PrepareImportAdmittedAsync(transition, backup, admission, CancellationToken.None);

        var prepared = fixture.Containers[^1];
        Assert.Equal(DataGenerationScopeState.Staged, candidate.State);
        Assert.Equal(23456, prepared.Session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        Assert.Equal(12345, repositories.Session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        Assert.All(prepared.Session.Snapshot.Applied.Values, state => Assert.Equal(SettingAppliedStateKind.Unknown, state.Kind));
        Assert.Equal(SettingsRegistry.Default.Definitions.Count, prepared.Session.Snapshot.PendingApplications.Sum(batch => batch.Entries.Count));
        Assert.Equal("retained log", Assert.Single(prepared.Logs.GetRecentLogs(5)).Message);
        Assert.Contains(prepared.Profiles.GetProfiles(), item => item.Id == imported.ProfileId);
        Assert.Contains("MATCH,REJECT", await File.ReadAllTextAsync(prepared.Configuration.GetProfileConfigurationPath(imported.ProfileId)), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(candidate.Descriptor.RootPath, "mihomo", "config.yaml")));
        Assert.Equal(sourceProfile, await File.ReadAllBytesAsync(imported.ConfigPath));
        Assert.Equal(sourceSelections, await File.ReadAllBytesAsync(selectionPath));
        Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        Assert.Throws<DataGenerationManagerException>(() => fixture.Manager.ReadSnapshot<SettingsAuthoritySession, SettingsEnvelope>((session, _) => session.Snapshot));
    }

    [Fact]
    public async Task FullResetPreparation_ResetsPreferencesAndRetainsProfilesLogsAndChoices()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        fixture.LegacyValues[SettingsRegistry.Keys.NotificationEnabled.Value] = false;
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        _ = await fixture.Containers[0].Profiles.AddSubscriptionLinkAsync("retained", "https://example.test/profile", CancellationToken.None);
        fixture.Containers[0].Logs.AppendLog("Info", "Reset", "retained", null);
        string relativeSelections = Path.Combine("mihomo", "proxy-selections.json");
        new ClashSharpUi::ClashSharp.Service.ProxySelectionStore(Path.Combine(baseline.Descriptor.RootPath, relativeSelections)).Save("profile-a", "GLOBAL", "Node B");
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);

        await using DataGenerationScope candidate = await fixture.CreateCandidatePreparer().PrepareResetAdmittedAsync(transition, admission, CancellationToken.None);

        var prepared = fixture.Containers[^1];
        Assert.All(SettingsRegistry.Default.GetResetDefinitions(SettingsResetScope.All), definition =>
            Assert.Equal(definition.DefaultValue, prepared.Session.Snapshot.Desired[definition.Key].Value));
        Assert.Equal("retained", Assert.Single(prepared.Profiles.GetSubscriptionLinks()).Name);
        Assert.Equal("retained", Assert.Single(prepared.Logs.GetRecentLogs(5)).Message);
        Assert.Equal("Node B", new ClashSharpUi::ClashSharp.Service.ProxySelectionStore(Path.Combine(candidate.Descriptor.RootPath, relativeSelections)).Read("profile-a")["GLOBAL"]);
        Assert.Equal(23456, fixture.Containers[0].Session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("scope")]
    [InlineData("duplicate")]
    [InlineData("port")]
    public async Task InvalidImport_IsRejectedBeforeCandidateAllocation(string fault)
    {
        await using DataGenerationTestDirectory directory = new();
        await using DataGenerationTestDirectory inputs = new();
        await using Fixture fixture = new(directory);
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        XDocument package = EmptyPackage();
        XElement settings = package.Root!.Element("Settings")!;
        if (fault is "duplicate" or "port")
        {
            settings.Add(new XElement("Setting", new XAttribute("Name", "MixedPort"), new XAttribute("Value", fault == "port" ? "0" : "12345")));
            if (fault == "duplicate") { settings.Add(new XElement(settings.Elements().Single())); }
        }
        else
        {
            package.Root.SetAttributeValue("Scope", fault == "scope" ? "Settings" : "SettingsAndProxyConfiguration");
            package.Root.Element("Files")!.Add(new XElement("File", new XAttribute("Path", fault == "scope" ? "ProfileCatalog.json" : "../escape.json"), Convert.ToBase64String("{}"u8.ToArray())));
        }
        Directory.CreateDirectory(inputs.RootPath);
        string input = Path.Combine(inputs.RootPath, "invalid.xml");
        await File.WriteAllTextAsync(input, package.ToString());
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.CreateCandidatePreparer().PrepareImportAdmittedAsync(transition, input, admission, CancellationToken.None)));

        Assert.Single(Directory.GetDirectories(directory.Policy.GenerationsRootPath));
        Assert.Single(fixture.Containers);
        Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    [Fact]
    public async Task InvalidImportedCatalog_RetiresCandidateResourcesWithoutPublishing()
    {
        await using DataGenerationTestDirectory directory = new();
        await using DataGenerationTestDirectory inputs = new();
        await using Fixture fixture = new(directory);
        DataGenerationManifestSnapshot baseline = await fixture.StartAsync();
        XDocument package = EmptyPackage();
        package.Root!.SetAttributeValue("Scope", "SettingsAndProxyConfiguration");
        package.Root.Element("Files")!.Add(new XElement("File", new XAttribute("Path", "ProfileCatalog.json"), Convert.ToBase64String("{broken"u8.ToArray())));
        Directory.CreateDirectory(inputs.RootPath);
        string input = Path.Combine(inputs.RootPath, "invalid-catalog.xml");
        await File.WriteAllTextAsync(input, package.ToString());
        await using MutationAdmissionLease admission = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await using DataGenerationTransition transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.CreateCandidatePreparer().PrepareImportAdmittedAsync(transition, input, admission, CancellationToken.None)));

        Assert.Equal(2, fixture.Containers.Count);
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[1].Session.Snapshot);
        Assert.Throws<ObjectDisposedException>(() => fixture.Containers[1].Logs.GetStorageSummary());
        Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        Assert.NotEmpty(fixture.Containers[0].Profiles.GetProfiles());
    }

    private static XElement PackageSettingElement(XDocument package, SettingKey key) => package.Root!.Element("Settings")!.Elements("Setting")
        .Single(setting => (string?)setting.Attribute("Name") == key.Value);
    private static string PackageSetting(XDocument package, SettingKey key) => (string)PackageSettingElement(package, key).Attribute("Value")!;
    private static XDocument EmptyPackage() => new(new XElement("ClashSharpDataPackage", new XAttribute("Format", "ClashSharp.XmlDataPackage"),
        new XAttribute("Version", "1"), new XAttribute("Scope", "Settings"), new XElement("Settings"), new XElement("Files")));

    private sealed partial class Fixture
    {
        public Preparer CreateCandidatePreparer() => new(_directory.RootPath, Admission, _factory);
    }
}

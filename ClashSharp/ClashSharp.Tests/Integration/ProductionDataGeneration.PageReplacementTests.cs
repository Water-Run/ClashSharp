extern alias ClashSharpUi;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;
using UiComposition = ClashSharpUi::ClashSharp.Presentation.Composition;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Theory]
    [InlineData(SettingsResetScope.Startup)]
    [InlineData(SettingsResetScope.Proxy)]
    [InlineData(SettingsResetScope.TransparentProxy)]
    public async Task ProductionRuntimeGroupReset_UsesOwnedRuntimeAndKeepsTheCurrentDataDirectory(SettingsResetScope scope)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.LaunchAtStartupEnabled.Value] = true;
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        fixture.LegacyValues[SettingsRegistry.Keys.TransparentProxyEnabled.Value] = false;
        fixture.LegacyValues[SettingsRegistry.Keys.ConnectionSamplingEnabled.Value] = false;
        fixture.LegacyValues[SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds.Value] = 90;
        var legacy = fixture.LegacyValues.OrderBy(pair => pair.Key).ToArray();
        NetworkSurface network = new();
        var settings = await StartReplacementFixtureAsync(fixture, network);
        var manifest = fixture.Manager.CurrentManifest;

        var result = await fixture.Authority.ResetRuntimeGroupAsync(scope, true, CancellationToken.None);

        Assert.True(result.Outcome.IsSucceeded, result.Outcome.Code);
        Assert.Equal(manifest, fixture.Manager.CurrentManifest);
        Assert.Equal(legacy, fixture.LegacyValues.OrderBy(pair => pair.Key).ToArray());
        Assert.Equal(scope == SettingsResetScope.Proxy ? 10000 : 23456, settings.MixedPort);
        Assert.Equal(scope != SettingsResetScope.Startup, settings.LaunchAtStartupEnabled);
        Assert.Equal(scope != SettingsResetScope.Startup, settings.TransparentProxyEnabled);
        Assert.Equal(scope == SettingsResetScope.Proxy, settings.ConnectionSamplingEnabled);
        Assert.Equal(scope == SettingsResetScope.Proxy ? 30 : 90, settings.ConnectionSamplingIntervalSeconds);
        Assert.All(SettingsRegistry.Default.GetResetDefinitions(scope), definition =>
        {
            Assert.Equal(definition.DefaultValue, result.Outcome.Envelope!.Desired[definition.Key].Value);
            Assert.Equal(SettingAppliedStateKind.Verified, result.Outcome.Envelope.Applied[definition.Key].Kind);
        });
        await using var lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        var actual = await RuntimeOf(fixture).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);
        Assert.Equal(settings.LaunchAtStartupEnabled, actual.StartupEnabled);
        Assert.Equal(settings.MixedPort, actual.Network.MixedPort);
        Assert.Equal(settings.TransparentProxyEnabled, actual.Network.TransparentProxyEnabled);
    }

    [Fact]
    public async Task PageImport_ReachesOwnedReplacementAndPublishesTheNewSettings()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var settings = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        var before = fixture.Manager.CurrentManifest;
        XDocument package = EmptyPackage();
        package.Root!.Element("Settings")!.Add(new XElement("Setting", new XAttribute("Name", "MixedPort"), new XAttribute("Value", "34567")));
        string input = Path.Combine(directory.RootPath, "page-import.xml");
        await File.WriteAllTextAsync(input, package.ToString());
        UiService.RestartRequiredStateService restart = new();
        UiData.GenerationSettingsDataReplacement replacement = new(fixture.CreateReplacementCoordinator(), restart.RequireRestart,
            _ => throw new InvalidOperationException("Unexpected recovery restart."));
        UiComposition.SettingsPageOperations page = new(new UiData.GenerationLogStorage(fixture.Manager),
            new UiData.GenerationDataPackageExporter(fixture.Manager, fixture.Admission, directory.RootPath), replacement,
            new ReplacementPageErrorSink(), new SettingsExportCoordinator(fixture.Admission));

        var result = await page.ImportDataPackageAsync(input, CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.False(result.RequiresRestart);
        Assert.False(restart.IsRestartPending);
        Assert.NotEqual(before.Descriptor.GenerationId, fixture.Manager.CurrentManifest.Descriptor.GenerationId);
        Assert.Equal(34567, settings.MixedPort);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageReplacement_PreservesCommittedWarningAndRestartAcrossPageReload(bool observerFails)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        fixture.Authority.StateChanged += _ => throw new IOException("view notification failed");
        UiService.RestartRequiredStateService restart = new();
        if (observerFails) { restart.RestartPendingChanged += (_, _) => throw new IOException("shell unavailable"); }
        UiData.GenerationSettingsDataReplacement page = new(fixture.CreateReplacementCoordinator(), restart.RequireRestart,
            _ => throw new InvalidOperationException("A committed warning should not force a restart."));

        var result = await page.ResetAllSettingsAsync(CancellationToken.None);

        Assert.True(result.RequiresRestart);
        Assert.Contains("data.replacement.notification_failed", result.Warnings);
        Assert.Equal(observerFails, result.Warnings.Contains("data.replacement.restart_notification_failed"));
        restart.SetRestartPending(false);
        Assert.True(restart.IsRestartPending);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageReplacement_RecoveryFailureRequestsOneRestartAndPreservesFailure(bool restartThrows)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        fixture.LegacyValues[SettingsRegistry.Keys.CurrentMode.Value] = (int)ClashSharpMode.RuleTakeover;
        NetworkSurface network = new();
        var settings = await StartReplacementFixtureAsync(fixture, network);
        network.BeforeApply = (target, _) =>
        {
            if (target.MixedPort == 10000 || target.Mode == ClashSharpMode.RuleTakeover) { throw new IOException("native state cannot be restored"); }
        };
        UiService.RestartRequiredStateService restart = new();
        List<string> reasons = [];
        UiData.GenerationSettingsDataReplacement page = new(fixture.CreateReplacementCoordinator(), restart.RequireRestart, reason =>
        {
            reasons.Add(reason);
            if (restartThrows) { throw new IOException("restart unavailable"); }
            return false;
        });

        var failure = await Assert.ThrowsAsync<SettingsDataReplacementRecoveryException>(() => page.ResetAllSettingsAsync(CancellationToken.None));

        Assert.False(failure.IsCommitted);
        Assert.Equal(["settings-reset-recovery"], reasons);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.True(restart.IsRestartPending);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Admission.State);
        Assert.Equal(23456, settings.MixedPort);
        Assert.Throws<DataGenerationManagerException>(() => new UiData.GenerationLogStorage(fixture.Manager).GetRecentLogs(1));
    }

    [Fact]
    public async Task PageReplacement_CancellationBeforeAdmissionLeavesCurrentGenerationAndRestartStateAlone()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        var before = fixture.Manager.CurrentManifest;
        UiData.GenerationSettingsDataReplacement page = new(fixture.CreateReplacementCoordinator(),
            () => throw new InvalidOperationException("Unexpected restart flag."), _ => throw new InvalidOperationException("Unexpected restart."));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.ResetAllSettingsAsync(cancellation.Token));
        Assert.Equal(before, fixture.Manager.CurrentManifest);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task SettingsDisplay_ReadsPublishedSnapshotDuringDrainButNeverAfterDisposal()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        fixture.LegacyValues[SettingsRegistry.Keys.ConnectionSamplingEnabled.Value] = false;
        fixture.LegacyValues[SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds.Value] = 90;
        var settings = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        await using (var transition = await fixture.Manager.BeginDrainAsync(fixture.Manager.CurrentManifest.ContentHash, CancellationToken.None))
        {
            Assert.Equal(23456, settings.MixedPort);
            var pair = settings.ReadConnectionSamplingSettings();
            Assert.False(pair.Enabled);
            Assert.Equal(90, pair.IntervalSeconds);
            Assert.Single(settings.ReadPreferenceChanges([SettingsRegistry.Keys.NotificationEnabled]));
            Assert.Throws<DataGenerationManagerException>(() => fixture.Authority.CaptureSnapshot());
            await transition.AbortAsync(directory.Store, CancellationToken.None);
        }
        await fixture.Manager.DisposeAsync();
        var failure = Assert.Throws<DataGenerationManagerException>(() => settings.MixedPort);
        Assert.Equal(DataGenerationManagerError.Disposed, failure.Error);
    }

    private sealed class ReplacementPageErrorSink : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected page error.");
    }
}

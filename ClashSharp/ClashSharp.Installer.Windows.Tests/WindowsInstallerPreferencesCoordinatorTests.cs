using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Model;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerPreferencesCoordinatorTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public void CanonicalIntentRoundTripsAndRejectsUnknownFieldsOrAnotherFamily()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        var document = new WindowsInstallerPreferencesDocument(1, Guid.NewGuid().ToString("N"),
            fixture.Manifest.PackageIdentity.PackageFamilyName, fixture.Manifest.ExpectedPackageVersion,
            fixture.Manifest.InstallerPayloadSha256, InstallerPreferences.Default with { EnableMcpServer = true });
        string encoded = WindowsInstallerPreferencesStore.Encode(document, document.PackageFamilyName);
        Assert.Equal(document, WindowsInstallerPreferencesStore.Decode(encoded, document.PackageFamilyName));
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPreferencesStore.Decode(encoded, document.PackageFamilyName + "other"));
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPreferencesStore.Decode(encoded[..^1] + ",\"unknown\":true}", document.PackageFamilyName));
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPreferencesStore.Decode(encoded + " ", document.PackageFamilyName));
    }

    [Fact]
    public void InterruptedUserOnlyIntentPreservesSelectionsAndCannotSwitchTheProtectedServiceProfile()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        MemoryStore store = new();
        var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new Shell(), setup: new Setup());
        InstallerPreferences selected = InstallerPreferences.Default with { InstallTransparentProxyService = false, EnableMcpServer = true };
        WindowsInstallerPreferencesPlan prepared = coordinator.Prepare(InstallerOperation.Install, selected, null);
        InstallerRequest request = fixture.Request(targetSid: Sid) with { InstallTransparentProxyService = false };
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        WindowsInstallerPreferencesPlan resumed = coordinator.Prepare(InstallerOperation.Install, null, journal);
        Assert.Equal(prepared.Document!.Id, resumed.Document!.Id);
        Assert.Equal(selected, resumed.Document.Preferences);
        Assert.False(resumed.IncludeService);
        Assert.Throws<InstallerProtocolException>(() => coordinator.Prepare(InstallerOperation.Install,
            selected with { InstallTransparentProxyService = true }, journal));
        Assert.Equal(selected, store.Pending!.Preferences);
    }

    [Fact]
    public async Task FailedUserSetupLeavesTheCoreVerifiedIntentAndRetryDoesNotReinstallAnything()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        MemoryStore store = new();
        Setup setup = new() { Fail = true };
        var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new Shell(), setup: setup);
        WindowsInstallerPreferencesPlan plan = coordinator.Prepare(InstallerOperation.Install, InstallerPreferences.Default, null);
        Assert.False(coordinator.HasPending);
        WindowsInstallerPreferencesDocument verified = coordinator.ConfirmCore(plan.Document!);
        Assert.True(coordinator.HasPending);
        await Assert.ThrowsAsync<IOException>(() => coordinator.ApplyAsync(verified, CancellationToken.None));
        Assert.Equal(verified, store.Pending);
        Assert.Null(store.Installed);
        setup.Fail = false;
        Assert.Equal(InstallerPreferencesWarnings.None, await coordinator.ApplyAsync(coordinator.RequirePending(), CancellationToken.None));
        Assert.Null(store.Pending);
        Assert.Equal(verified, store.Installed);
        Assert.Equal(2, setup.Calls);
    }

    [Fact]
    public void ChangedSelectionsOrAnotherReleaseCannotReuseAnOldBootstrapReceiptIdentity()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        MemoryStore store = new();
        var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new Shell(), setup: new Setup());
        WindowsInstallerPreferencesDocument original = coordinator.Prepare(InstallerOperation.Install, InstallerPreferences.Default, null).Document!;
        WindowsInstallerPreferencesDocument changed = coordinator.Prepare(InstallerOperation.Install,
            InstallerPreferences.Default with { EnableMcpServer = true }, null).Document!;
        Assert.NotEqual(original.Id, changed.Id);
        store.Pending = changed with { PayloadSha256 = new string('a', 64) };
        WindowsInstallerPreferencesDocument nextRelease = coordinator.Prepare(InstallerOperation.Repair, null, null).Document!;
        Assert.NotEqual(changed.Id, nextRelease.Id);
        Assert.False(nextRelease.InitializeApplicationDefaults);
        Assert.False(nextRelease.CoreVerified);
    }

    [Fact]
    public void MaintenancePreservesComponentSelectionAndDoesNotReinitializeApplicationPreferences()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        MemoryStore store = new()
        {
            Installed = new(1, Guid.NewGuid().ToString("N"), fixture.Manifest.PackageIdentity.PackageFamilyName,
                fixture.Manifest.ExpectedPackageVersion, fixture.Manifest.InstallerPayloadSha256,
                InstallerPreferences.Default with { InstallTransparentProxyService = false, EnableMcpServer = true }, CoreVerified: true),
        };
        var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, new Shell(), setup: new Setup());
        WindowsInstallerPreferencesPlan repair = coordinator.Prepare(InstallerOperation.Repair, null, null);
        Assert.False(repair.IncludeService);
        Assert.False(repair.Document!.InitializeApplicationDefaults);
        Assert.True(repair.Document.Preferences.EnableMcpServer);
    }

    [Fact]
    public void GenericPresetSeedsOnlySelectedInitialValuesAndNeverGrantsAgentProxyControl()
    {
        InstallerPreferences preferences = InstallerPreferences.Default with
        { FollowSystemDefaults = false, EnableMcpServer = true, Notifications = InstallerNotificationPreset.CriticalOnly };
        IReadOnlyDictionary<string, object> values = WindowsInstallerPreferencesCoordinator.BuildSeed(preferences);
        Assert.Equal((int)AppLanguage.English, values["DisplayLanguage"]);
        Assert.Equal((int)ClockFormat.TwentyFourHour, values["ClockFormat"]);
        Assert.Equal((int)MainlandChinaFeatureMode.Disabled, values["MainlandChinaFeatureMode"]);
        Assert.Equal((int)NotificationLevel.CriticalOnly, values["NotificationLevel"]);
        Assert.Equal(true, values["McpServerEnabled"]);
        Assert.Equal(1, values["McpAllowedScopes"]);
        Assert.Equal(false, values["LaunchAtStartupEnabled"]);
        Assert.Equal(true, values["TrayUseMonochromeInactiveIcon"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalClearsOnlyCapturedUserIntentAfterOwnedShortcutCleanup(bool failShell)
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        MemoryStore store = new();
        Shell shell = new() { FailRemoval = failShell };
        Setup setup = new();
        var coordinator = new WindowsInstallerPreferencesCoordinator(fixture.Manifest, Sid, store, shell, setup: setup);
        coordinator.Prepare(InstallerOperation.Install, InstallerPreferences.Default, null);
        WindowsInstallerPreferencesPlan plan = coordinator.Prepare(InstallerOperation.Uninstall, null, null);
        if (failShell)
        {
            await Assert.ThrowsAsync<IOException>(() => coordinator.RemoveAsync(plan, CancellationToken.None));
            Assert.Equal(plan.Pending, store.Pending);
        }
        else
        {
            await coordinator.RemoveAsync(plan, CancellationToken.None);
            Assert.Null(store.Pending);
            Assert.Null(store.Installed);
        }
        Assert.Equal(fixture.Manifest.PackageIdentity.PackageFamilyName + "!App", shell.RemovedApplicationId);
        Assert.Equal(0, setup.Calls);
    }

    private sealed class MemoryStore : IWindowsInstallerPreferencesStore
    {
        internal WindowsInstallerPreferencesDocument? Pending { get; set; }
        internal WindowsInstallerPreferencesDocument? Installed { get; set; }
        public WindowsInstallerPreferencesDocument? ReadPending() => Pending;
        public WindowsInstallerPreferencesDocument? ReadInstalled() => Installed;
        public void Stage(WindowsInstallerPreferencesDocument document, WindowsInstallerPreferencesDocument? expected)
        { Assert.Equal(expected, Pending); Pending = document; }
        public void Complete(WindowsInstallerPreferencesDocument document)
        { Assert.Equal(document, Pending); Installed = document; Pending = null; }
        public void RemoveInstalled(WindowsInstallerPreferencesDocument? expected) { Assert.Equal(expected, Installed); Installed = null; }
        public void RemovePending(WindowsInstallerPreferencesDocument? expected) { Assert.Equal(expected, Pending); Pending = null; }
    }

    private sealed class Setup : IWindowsInstallerUserSetup
    {
        internal bool Fail { get; set; }
        internal int Calls { get; private set; }
        public Task<InstallerPreferencesWarnings> ApplyAsync(WindowsInstallerPreferencesDocument document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Fail ? Task.FromException<InstallerPreferencesWarnings>(new IOException("Synthetic user setup failure."))
                : Task.FromResult(InstallerPreferencesWarnings.None);
        }
    }

    private sealed class Shell : IWindowsInstallerShellIntegration
    {
        internal bool FailRemoval { get; init; }
        internal string? RemovedApplicationId { get; private set; }
        public Task ApplyAsync(InstallerPreferences preferences, string applicationId, string iconPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string applicationId, CancellationToken cancellationToken)
        {
            RemovedApplicationId = applicationId;
            return FailRemoval ? Task.FromException(new IOException("Synthetic shortcut cleanup failure.")) : Task.CompletedTask;
        }
    }
}

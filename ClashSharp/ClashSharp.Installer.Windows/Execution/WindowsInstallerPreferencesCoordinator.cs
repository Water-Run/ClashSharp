using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Model;
using ClashSharp.Platform.Regional;
using ClashSharp.Settings;

namespace ClashSharp.Installer.Windows.Execution;

internal sealed record WindowsInstallerPreferencesPlan(bool IncludeService,
    WindowsInstallerPreferencesDocument? Document, WindowsInstallerPreferencesDocument? Installed,
    WindowsInstallerPreferencesDocument? Pending);

/// <summary>Owns resumable user setup after, and separately from, the protected installation transaction.</summary>
internal sealed class WindowsInstallerPreferencesCoordinator
{
    private readonly InstallerReleaseManifest _manifest;
    private readonly IWindowsInstallerPreferencesStore _store;
    private readonly IWindowsInstallerShellIntegration _shell;
    private readonly IWindowsInstallerUserSetup _setup;

    internal WindowsInstallerPreferencesCoordinator(InstallerReleaseManifest manifest, string sid,
        IWindowsInstallerPreferencesStore? store = null, IWindowsInstallerShellIntegration? shell = null,
        IWindowsPackagedSetupLauncher? launcher = null, IWindowsInstallerUserSetup? setup = null)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _store = store ?? new WindowsInstallerPreferencesStore(manifest, sid);
        _shell = shell ?? new WindowsInstallerShellIntegration();
        _setup = setup ?? new WindowsInstallerUserSetup(manifest, sid, _shell, launcher ?? new WindowsPackagedSetupLauncher());
    }

    internal bool HasPending => _store.ReadPending() is { CoreVerified: true } pending && MatchesRelease(pending);
    internal bool UsesService => (_store.ReadPending() ?? _store.ReadInstalled())?.Preferences.InstallTransparentProxyService ?? true;

    internal WindowsInstallerPreferencesPlan Prepare(InstallerOperation operation, InstallerPreferences? selected,
        InstallerTransactionJournal? durable)
    {
        WindowsInstallerPreferencesDocument? pending = _store.ReadPending(), installed = _store.ReadInstalled();
        if (operation == InstallerOperation.Uninstall)
        {
            return new(durable?.InstallTransparentProxyService ?? pending?.Preferences.InstallTransparentProxyService
                ?? installed?.Preferences.InstallTransparentProxyService ?? true, null, installed, pending);
        }
        InstallerPreferences? preferences = selected ?? pending?.Preferences ?? installed?.Preferences;
        bool includeService = durable?.InstallTransparentProxyService ?? preferences?.InstallTransparentProxyService ?? true;
        if (preferences is null) { return new(includeService, null, installed, pending); }
        preferences.Validate();
        if (includeService != preferences.InstallTransparentProxyService) { throw Failure("service_profile_conflict"); }
        if (durable is not null && (pending is null || !MatchesRelease(pending)
            || selected is not null && selected != pending.Preferences)) { throw Failure("pending_intent_conflict"); }
        bool resumesIntent = pending is not null && MatchesRelease(pending) && preferences == pending.Preferences;
        WindowsInstallerPreferencesDocument document = new(1, resumesIntent ? pending!.Id : Guid.NewGuid().ToString("N"),
            _manifest.PackageIdentity.PackageFamilyName, _manifest.ExpectedPackageVersion, _manifest.InstallerPayloadSha256,
            preferences, selected is not null || resumesIntent && pending!.InitializeApplicationDefaults, CoreVerified: false);
        _store.Stage(document, pending);
        return new(includeService, document, installed, pending);
    }

    internal WindowsInstallerPreferencesDocument ConfirmCore(WindowsInstallerPreferencesDocument document)
    {
        WindowsInstallerPreferencesDocument verified = document with { CoreVerified = true };
        _store.Stage(verified, document);
        return verified;
    }

    internal WindowsInstallerPreferencesDocument RequirePending()
    {
        WindowsInstallerPreferencesDocument pending = _store.ReadPending() ?? throw Failure("pending_missing");
        if (!pending.CoreVerified || !MatchesRelease(pending)) { throw Failure("core_verification_required"); }
        return pending;
    }

    internal async Task<InstallerPreferencesWarnings> ApplyAsync(WindowsInstallerPreferencesDocument document, CancellationToken cancellationToken)
    {
        if (!document.CoreVerified || !MatchesRelease(document) || _store.ReadPending() != document) { throw Failure("pending_intent_conflict"); }
        InstallerPreferencesWarnings warnings = await _setup.ApplyAsync(document, cancellationToken).ConfigureAwait(false);
        _store.Complete(document);
        return warnings;
    }

    internal async Task RemoveAsync(WindowsInstallerPreferencesPlan plan, CancellationToken cancellationToken)
    {
        await _shell.RemoveAsync(_manifest.PackageIdentity.PackageFamilyName + "!App", cancellationToken).ConfigureAwait(false);
        _store.RemoveInstalled(plan.Installed);
        _store.RemovePending(plan.Pending);
    }

    internal static IReadOnlyDictionary<string, object> BuildSeed(InstallerPreferences preferences)
    {
        Dictionary<string, object> seed = new(preferences.FollowSystemDefaults ? WindowsRegionalDefaults.Capture()
            : RegionalDefaultsPolicy.Create("en-US", "US", "UTC", "24HourClock"), StringComparer.Ordinal);
        if (!preferences.FollowSystemDefaults) { seed[SettingsRegistry.Keys.DisplayLanguage.Value] = (int)AppLanguage.English; }
        seed[SettingsRegistry.Keys.LaunchAtStartupEnabled.Value] = preferences.LaunchAtStartup;
        seed[SettingsRegistry.Keys.NotificationEnabled.Value] = preferences.Notifications != InstallerNotificationPreset.Disabled;
        seed[SettingsRegistry.Keys.NotificationLevel.Value] = preferences.Notifications switch
        {
            InstallerNotificationPreset.CriticalOnly => (int)NotificationLevel.CriticalOnly,
            InstallerNotificationPreset.All => (int)NotificationLevel.More,
            _ => (int)NotificationLevel.Default,
        };
        seed[SettingsRegistry.Keys.TransparentProxyEnabled.Value] = preferences.InstallTransparentProxyService;
        seed[SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon.Value] = true;
        seed[SettingsRegistry.Keys.McpServerEnabled.Value] = preferences.EnableMcpServer;
        seed[SettingsRegistry.Keys.McpAllowedScopes.Value] = 1;
        return seed;
    }

    private bool MatchesRelease(WindowsInstallerPreferencesDocument document) => document.PackageVersion == _manifest.ExpectedPackageVersion
        && document.PayloadSha256 == _manifest.InstallerPayloadSha256;

    private static InstallerProtocolException Failure(string code) => new("installer.preferences." + code);
}

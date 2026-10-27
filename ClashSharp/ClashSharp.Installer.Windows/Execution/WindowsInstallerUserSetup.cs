using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Platform.Regional;
using ClashSharp.Settings;
using Windows.Management.Core;
using Windows.Storage;

namespace ClashSharp.Installer.Windows.Execution;

internal interface IWindowsInstallerUserSetup
{
    Task<InstallerPreferencesWarnings> ApplyAsync(WindowsInstallerPreferencesDocument document, CancellationToken cancellationToken);
}

/// <summary>Applies package-local defaults, native login tasks and current-user shell integration.</summary>
internal sealed class WindowsInstallerUserSetup(InstallerReleaseManifest manifest, string sid,
    IWindowsInstallerShellIntegration shell, IWindowsPackagedSetupLauncher launcher) : IWindowsInstallerUserSetup
{
    private readonly InstallerReleaseManifest _manifest = manifest;
    private readonly string _sid = sid;
    private readonly IWindowsInstallerShellIntegration _shell = shell;
    private readonly IWindowsPackagedSetupLauncher _launcher = launcher;

    public async Task<InstallerPreferencesWarnings> ApplyAsync(WindowsInstallerPreferencesDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string family = _manifest.PackageIdentity.PackageFamilyName;
        InstallerInstalledPackage installed = WindowsPackageRegistrationInspector.Inspect(new WindowsPackageManagerFacade(), _sid, _manifest)
            ?? throw Failure("package_missing");
        if (!installed.IsHealthy || installed.Version != _manifest.ExpectedPackageVersion) { throw Failure("package_mismatch"); }
        IReadOnlyList<string> roots = new WindowsInstalledPackageFootprintCatalog().FindInstalledRoots(family, installed.PackageFullName, cancellationToken);
        if (roots.Count != 1) { throw Failure("package_root_unconfirmed"); }
        string applicationId = family + "!App";
        string executable = Path.Combine(roots[0], "ClashSharp.exe");
        string icon = Path.Combine(roots[0], "Assets", "Tray", "Logo.SystemProxy.ico");
        await _shell.ApplyAsync(document.Preferences, applicationId, icon, cancellationToken).ConfigureAwait(false);
        ApplicationData data = ApplicationDataManager.CreateForPackageFamily(family);
        InstallerPreferencesWarnings warnings = InstallerPreferencesWarnings.None;
        if (document.InitializeApplicationDefaults)
        {
            bool priorCompletion = data.LocalSettings.Values.TryGetValue(InstallationSetupProtocol.ReceiptKey, out object? prior)
                && prior is ApplicationDataCompositeValue priorReceipt && priorReceipt.TryGetValue("id", out object? priorId)
                && Equals(priorId, document.Id);
            bool initialized = !priorCompletion && WindowsRegionalDefaults.InitializeSelected(data, WindowsInstallerPreferencesCoordinator.BuildSeed(document.Preferences));
            if (priorCompletion) { warnings |= ReadReceipt(data, document.Id); }
            else if (!initialized) { warnings |= InstallerPreferencesWarnings.ExistingSettingsPreserved; }
            if (initialized)
            {
                data.LocalSettings.Values.Remove(InstallationSetupProtocol.ReceiptKey);
                data.LocalSettings.Values[InstallationSetupProtocol.RequestKey] = new ApplicationDataCompositeValue
                {
                    ["schema"] = 1,
                    ["id"] = document.Id,
                    ["startup"] = document.Preferences.LaunchAtStartup,
                    ["recovery"] = document.Preferences.RegisterProxyRecovery,
                    ["applyDefaults"] = true,
                };
            }
            object? stagedRequest = data.LocalSettings.Values.TryGetValue(InstallationSetupProtocol.RequestKey, out object? request) ? request : null;
            if (!priorCompletion && ShouldRunBootstrap(initialized, stagedRequest, document))
            {
                await _launcher.RunAsync(applicationId, executable, document.Id, cancellationToken).ConfigureAwait(false);
                warnings |= ReadReceipt(data, document.Id);
            }
            if (document.Preferences.RequestTaskbarPin) { StagePin(data, InstallationSetupProtocol.TaskbarRequestKey, InstallationSetupProtocol.TaskbarReceiptKey, document.Id); }
            if (document.Preferences.RequestTaskbarPin)
            {
                warnings |= InstallerPreferencesWarnings.NativePinConfirmationPending;
            }
        }
        return warnings;
    }

    // A user may open the installed application before retrying unfinished login-task
    // setup. Defaults have then been consumed, but the correlated native work remains owned.
    internal static bool ShouldRunBootstrap(bool initialized, object? raw, WindowsInstallerPreferencesDocument document)
    {
        if (initialized) { return true; }
        if (raw is null) { return false; }
        if (raw is not ApplicationDataCompositeValue request || request.Count != 5
            || !request.TryGetValue("schema", out object? schema) || schema is not 1
            || !request.TryGetValue("id", out object? id) || !Equals(id, document.Id)
            || !request.TryGetValue("startup", out object? startup) || !Equals(startup, document.Preferences.LaunchAtStartup)
            || !request.TryGetValue("recovery", out object? recovery) || !Equals(recovery, document.Preferences.RegisterProxyRecovery)
            || !request.TryGetValue("applyDefaults", out object? apply) || apply is not true)
        { throw Failure("bootstrap_request_conflict"); }
        return true;
    }

    private static void StagePin(ApplicationData data, string requestKey, string receiptKey, string id)
    {
        if (data.LocalSettings.Values.TryGetValue(receiptKey, out object? handled) && Equals(handled, id)) { return; }
        data.LocalSettings.Values[requestKey] = id;
    }

    private static InstallerPreferencesWarnings ReadReceipt(ApplicationData data, string id)
    {
        if (!data.LocalSettings.Values.TryGetValue(InstallationSetupProtocol.ReceiptKey, out object? value)
            || value is not ApplicationDataCompositeValue receipt || receipt.Count != 3
            || !receipt.TryGetValue("schema", out object? schema) || schema is not 1
            || !receipt.TryGetValue("id", out object? receiptId) || !Equals(receiptId, id)
            || !receipt.TryGetValue("warnings", out object? raw) || raw is not int bits || bits < 0
            || (bits & ~InstallationSetupProtocol.KnownWarningBits) != 0) { throw Failure("bootstrap_unconfirmed"); }
        return (InstallerPreferencesWarnings)bits;
    }

    private static InstallerProtocolException Failure(string code) => new("installer.preferences." + code);
}

using System.Globalization;
using System.Windows;
using ClashSharp.Installer.Localization;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer;

/// <summary>Creates the presentation shell without granting it implicit mutation authority.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows chooses the display language; the user can still pick another one before an operation starts.
        InstallerText text = new(InstallerText.Detect(CultureInfo.CurrentUICulture));
        InstallerLocalization.Current = text;
        IInstallerRuntime runtime = CreateRuntime(text, out string? packageVersion);
#if CLASHSHARP_INSTALLER_INTERNAL_CANDIDATE
        const bool internalCandidate = true;
#else
        const bool internalCandidate = false;
#endif
        var viewModel = new InstallerShellViewModel(runtime, packageVersion, internalCandidate, text);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    private static IInstallerRuntime CreateRuntime(InstallerText text, out string? packageVersion)
    {
#if CLASHSHARP_INSTALLER_MUTATION_RUNTIME
        EmbeddedInstallerReleaseManifest release = EmbeddedInstallerReleaseManifest.Load();
        packageVersion = release.Manifest.ExpectedPackageVersion;
        return WindowsProductionInstallerRuntimeFactory.Create(release, text);
#else
        packageVersion = null;
        _ = text;
        return new MigrationPreviewInstallerRuntime();
#endif
    }
}

using System.Windows;
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

        IInstallerRuntime runtime = CreateRuntime(out string? packageVersion);
#if CLASHSHARP_INSTALLER_INTERNAL_CANDIDATE
        const bool internalCandidate = true;
#else
        const bool internalCandidate = false;
#endif
        var viewModel = new InstallerShellViewModel(runtime, packageVersion, internalCandidate);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    private static IInstallerRuntime CreateRuntime(out string? packageVersion)
    {
#if CLASHSHARP_INSTALLER_MUTATION_RUNTIME
        EmbeddedInstallerReleaseManifest release = EmbeddedInstallerReleaseManifest.Load();
        packageVersion = release.Manifest.ExpectedPackageVersion;
        return WindowsProductionInstallerRuntimeFactory.Create(release);
#else
        packageVersion = null;
        return new MigrationPreviewInstallerRuntime();
#endif
    }
}

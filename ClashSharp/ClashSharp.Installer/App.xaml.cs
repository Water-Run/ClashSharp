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

        IInstallerRuntime runtime = new MigrationPreviewInstallerRuntime();
        var viewModel = new InstallerShellViewModel(runtime);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }
}

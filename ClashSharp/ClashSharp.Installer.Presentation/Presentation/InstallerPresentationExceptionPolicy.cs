namespace ClashSharp.Installer.Presentation;

internal static class InstallerPresentationExceptionPolicy
{
    internal static bool IsRecoverable(Exception exception) =>
        !ClashSharp.Installer.Contracts.InstallerExceptionPolicy.IsProcessFatal(exception);
}

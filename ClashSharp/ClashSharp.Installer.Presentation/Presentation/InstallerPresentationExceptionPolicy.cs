namespace ClashSharp.Installer.Presentation;

internal static class InstallerPresentationExceptionPolicy
{
    internal static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

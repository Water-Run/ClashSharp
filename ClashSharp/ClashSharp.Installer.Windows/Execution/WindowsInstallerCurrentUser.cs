using System.Security.Principal;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Execution;

internal static class WindowsInstallerCurrentUser
{
    internal static string GetSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            string sid = identity.User?.Value
                ?? throw new InstallerProtocolException(
                    "installer.environment.current_user_invalid");
            InstallerProtocolValidation.ValidateTargetSid(sid);
            return sid;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.environment.current_user_invalid",
                exception);
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

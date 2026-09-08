using System.Security.AccessControl;
using System.Security.Principal;

namespace ClashSharp.MihomoService;

/// <summary>Validates service-private roots and the cache objects created by the privileged child.</summary>
internal static class MihomoRuntimeSecurityPolicy
{
    /// <summary>
    /// Keeps roots LocalSystem-owned while accepting the Administrators owner used by native
    /// child-created cache objects. Both principals already control the private parent directory;
    /// every object must still exclude other principals and grant LocalSystem full access.
    /// Validation observes the descriptor without changing live cache ownership or permissions.
    /// </summary>
    internal static void Validate(FileSystemSecurity security, bool allowAdministratorOwner)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || (!owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                && !(allowAdministratorOwner && owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))))
        {
            throw new UnauthorizedAccessException("The protected runtime object has an untrusted owner.");
        }

        if (!allowAdministratorOwner && !security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException("The protected runtime root must exclude inherited permissions.");
        }

        bool systemCanFullyControl = false;
        foreach (AuthorizationRule authorizationRule in security.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (authorizationRule is not FileSystemAccessRule rule
                || rule.IdentityReference is not SecurityIdentifier sid
                || (!sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            {
                throw new UnauthorizedAccessException("The protected runtime object has an untrusted ACL.");
            }

            if (!sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            if (rule.AccessControlType == AccessControlType.Deny && rule.FileSystemRights != 0)
            {
                throw new UnauthorizedAccessException("The protected runtime object denies LocalSystem access.");
            }

            systemCanFullyControl |= rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
        }

        if (!systemCanFullyControl)
        {
            throw new UnauthorizedAccessException("The protected runtime object does not grant LocalSystem full control.");
        }
    }
}

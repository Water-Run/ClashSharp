using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;

namespace ClashSharp.Installer.Ownership;

/// <summary>Private owner evidence captured by the elevated helper from trusted machine state.</summary>
/// <param name="Association">Exact owner SID and IPC credential.</param>
/// <param name="ProfileRoot">Drive-qualified profile path resolved for that SID by Windows.</param>
public sealed record InstallerOwnerTransferParticipant(
    InstallerMachineAssociation Association,
    string ProfileRoot)
{
    /// <summary>Gets the maximum profile path length accepted by this bounded protocol.</summary>
    public const int MaximumProfilePathCharacters = 1024;

    /// <summary>
    /// Validates protocol shape only; the Windows authority must independently verify the SID's
    /// profile mapping, directory identity, ACL and absence of reparse ancestors.
    /// </summary>
    public void Validate()
    {
        if (Association is null)
        {
            throw new InstallerProtocolException("installer.owner_transfer.participant_invalid");
        }

        Association.Validate();
        if (ProfileRoot is null
            || ProfileRoot.Length is < 4 or > MaximumProfilePathCharacters
            || ProfileRoot[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z')
            || ProfileRoot[1] != ':'
            || ProfileRoot[2] != '\\'
            || HasInvalidUnicode(ProfileRoot)
            || ProfileRoot[3..].Any(static character =>
                char.IsControl(character) || character is '/' or ':' or '"' or '<' or '>' or '|' or '?' or '*')
            || ProfileRoot[3..].Split('\\').Any(static segment =>
                segment.Length == 0 || segment is "." or ".."
                || segment.EndsWith(' ') || segment.EndsWith('.') || IsDeviceName(segment)))
        {
            throw new InstallerProtocolException("installer.owner_transfer.profile_path_invalid");
        }
    }

    /// <summary>Returns a diagnostic description that never includes credentials or profile paths.</summary>
    public override string ToString() => "InstallerOwnerTransferParticipant { Private owner evidence }";

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }

    private static bool HasInvalidUnicode(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 == value.Length || !char.IsLowSurrogate(value[++index]))
                {
                    return true;
                }
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return true;
            }
        }

        return false;
    }
}

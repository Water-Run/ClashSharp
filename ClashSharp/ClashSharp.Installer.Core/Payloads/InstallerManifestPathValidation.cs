using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Payloads;

/// <summary>Shared canonical Windows-relative-path validation for signed manifest entries.</summary>
internal static class InstallerManifestPathValidation
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.Ordinal)
    {
        "aux",
        "clock$",
        "con",
        "nul",
        "prn",
        "com1",
        "com2",
        "com3",
        "com4",
        "com5",
        "com6",
        "com7",
        "com8",
        "com9",
        "lpt1",
        "lpt2",
        "lpt3",
        "lpt4",
        "lpt5",
        "lpt6",
        "lpt7",
        "lpt8",
        "lpt9",
    };

    internal static void ValidateCanonicalRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Length > InstallerPayloadBudgets.MaximumRelativePathCharacters
            || path[0] == '/'
            || path[^1] == '/'
            || path.Contains("//", StringComparison.Ordinal)
            || path.Any(static character => character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-'
                and not '_'
                and not '.'
                and not '/'))
        {
            throw new InstallerProtocolException("installer.release.manifest_path_invalid");
        }

        string[] segments = path.Split('/');
        if (segments.Length - 1 > InstallerPayloadBudgets.MaximumDirectoryDepth
            || segments.Any(static segment => segment is "." or ".."
                || segment.EndsWith('.')
                || segment.EndsWith(' ')))
        {
            throw new InstallerProtocolException("installer.release.manifest_path_invalid");
        }

        foreach (string segment in segments)
        {
            string stem = segment.Split('.', 2)[0];
            if (ReservedWindowsNames.Contains(stem))
            {
                throw new InstallerProtocolException("installer.release.manifest_path_invalid");
            }
        }
    }
}

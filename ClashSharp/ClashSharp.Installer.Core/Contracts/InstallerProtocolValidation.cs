using System.Globalization;

namespace ClashSharp.Installer.Contracts;

/// <summary>Provides canonical validation shared by requests and journals.</summary>
public static class InstallerProtocolValidation
{
    /// <summary>Validates a canonical four-component MSIX version.</summary>
    /// <param name="value">Version in <c>major.minor.build.revision</c> form.</param>
    public static void ValidatePackageVersion(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] parts = value.Split('.');
        if (parts.Length != 4
            || parts.Any(static part => part.Length == 0
                || part.Length > 1 && part[0] == '0'
                || !part.All(static character => character is >= '0' and <= '9')
                || !ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new InstallerProtocolException("installer.request.package_version_invalid");
        }
    }

    /// <summary>Validates a canonical Windows security identifier without resolving an account.</summary>
    /// <param name="value">String SID identifying the target interactive user.</param>
    public static void ValidateTargetSid(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] parts = value.Split('-');
        if (value.Length is < 7 or > 184
            || parts.Length is < 4 or > 18
            || !string.Equals(parts[0], "S", StringComparison.Ordinal)
            || parts.Skip(1).Any(static part => part.Length == 0
                || part.Length > 1 && part[0] == '0'
                || !part.All(static character => character is >= '0' and <= '9')
                || !ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new InstallerProtocolException("installer.request.target_sid_invalid");
        }

        bool canonicalRanges = string.Equals(parts[1], "1", StringComparison.Ordinal)
            && ulong.Parse(parts[2], CultureInfo.InvariantCulture) <= 0x0000_ffff_ffff_ffff
            && parts.Skip(3).All(static part =>
                uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        // The target is exactly one interactive user account. Well-known groups, service and logon-session
        // identities are not accounts, and a deny-list can never name them all: accepting one would let a
        // mistaken caller grant a whole group (for example Authenticated Users) rights meant for one user.
        // Local, domain and Microsoft-account users are S-1-5-21-<three domain sub-authorities>-<RID> and
        // Azure AD users are S-1-12-1-<four sub-authorities>; both have eight parts when split.
        bool isLocalOrDomainUser = parts.Length == 8
            && string.Equals(parts[1], "1", StringComparison.Ordinal)
            && string.Equals(parts[2], "5", StringComparison.Ordinal)
            && string.Equals(parts[3], "21", StringComparison.Ordinal);
        bool isAzureAdUser = parts.Length == 8
            && string.Equals(parts[1], "1", StringComparison.Ordinal)
            && string.Equals(parts[2], "12", StringComparison.Ordinal)
            && string.Equals(parts[3], "1", StringComparison.Ordinal);
        if (!canonicalRanges || !(isLocalOrDomainUser || isAzureAdUser))
        {
            throw new InstallerProtocolException("installer.request.target_sid_invalid");
        }
    }

    /// <summary>Validates a canonical lowercase 256-bit hexadecimal value.</summary>
    /// <param name="value">Value to validate.</param>
    /// <param name="diagnosticCode">Failure code to throw.</param>
    public static void ValidateLowerHex256(string value, string diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        if (value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InstallerProtocolException(diagnosticCode);
        }
    }

    /// <summary>Validates the canonical uppercase 160-bit thumbprint returned by Windows.</summary>
    /// <param name="value">Certificate SHA-1 thumbprint without spaces or separators.</param>
    /// <param name="diagnosticCode">Failure code to throw.</param>
    public static void ValidateUpperHex160(string value, string diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        if (value.Length != 40
            || value.Any(static character => character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'F')))
        {
            throw new InstallerProtocolException(diagnosticCode);
        }
    }

    /// <summary>Validates a bounded stable diagnostic code safe for IPC and presentation.</summary>
    /// <param name="value">Lowercase dotted machine-readable diagnostic code.</param>
    public static void ValidateDiagnosticCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 3 or > 128
            || value[0] == '.'
            || value[^1] == '.'
            || value.Contains("..", StringComparison.Ordinal)
            || value.Any(static character => character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '_'
                and not '.'))
        {
            throw new InstallerProtocolException("installer.diagnostic_code_invalid");
        }
    }

    /// <summary>Parses a canonical MSIX version after applying the stricter journal grammar.</summary>
    /// <param name="value">Canonical four-component MSIX version.</param>
    /// <returns>The parsed version.</returns>
    public static Version ParsePackageVersion(string value)
    {
        ValidatePackageVersion(value);
        return Version.Parse(value);
    }
}

using System.Globalization;
using System.Reflection;

namespace ClashSharp;

/// <summary>Provides the product version, source revision and build instant stamped into this assembly.</summary>
public static class ApplicationVersion
{
    /// <summary>Assembly metadata key written by the Core project file at build time.</summary>
    internal const string BuildTimestampMetadataKey = "ClashSharp.BuildTimestamp";

    private static readonly Assembly StampedAssembly = typeof(ApplicationVersion).Assembly;

    /// <summary>Gets the three-component version displayed by application pages.</summary>
    public static string Current { get; } = StampedAssembly.GetName().Version?.ToString(3)
        ?? "1.0.0";

    /// <summary>Gets the abbreviated source commit, or <see langword="null"/> when the build carried no revision.</summary>
    public static string? SourceRevision { get; } = ParseSourceRevision(
        StampedAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Gets the UTC build instant, or <see langword="null"/> when the stamp is missing or malformed.</summary>
    public static DateTimeOffset? BuildTimestamp { get; } = ParseBuildTimestamp(
        StampedAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static attribute => attribute.Key == BuildTimestampMetadataKey)?.Value);

    /// <summary>Extracts the first seven hexadecimal digits after the informational version's '+' separator.</summary>
    /// <param name="informationalVersion">Informational version such as <c>1.0.0+abcdef0</c>; may be null.</param>
    /// <returns>The lowercase abbreviated revision, or null when no hexadecimal revision is present.</returns>
    public static string? ParseSourceRevision(string? informationalVersion)
    {
        int separator = informationalVersion?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (informationalVersion is null || separator < 0)
        {
            return null;
        }

        string revision = informationalVersion[(separator + 1)..];
        return revision.Length >= 7 && revision.Take(7).All(char.IsAsciiHexDigit)
            ? revision[..7].ToLowerInvariant()
            : null;
    }

    /// <summary>Parses an ISO 8601 UTC build stamp.</summary>
    /// <param name="value">Stamp such as <c>2026-11-04T03:00:00Z</c>; may be null.</param>
    /// <returns>The instant in UTC, or null when the value is not a valid timestamp.</returns>
    public static DateTimeOffset? ParseBuildTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp)
            ? timestamp
            : null;
}

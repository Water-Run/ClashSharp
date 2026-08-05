using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.ViewModel;

/// <summary>Read-only application release check used by the about page.</summary>
/// <remarks>
/// Invariants: Implementations never install, download, or execute release assets.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May issue one bounded network request to the fixed project release API.
/// </remarks>
internal interface IApplicationUpdateChecker
{
    /// <summary>Gets the installed application version used for comparison.</summary>
    /// <value>A non-empty stable version string.</value>
    string CurrentVersion { get; }

    /// <summary>Checks whether a newer stable application release exists.</summary>
    /// <param name="cancellationToken">Cancels the release check.</param>
    /// <returns>A non-throwing availability result for recoverable network and payload failures.</returns>
    Task<ApplicationUpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Outcome of a read-only release availability check.</summary>
internal enum ApplicationUpdateAvailability
{
    /// <summary>The release service could not be checked safely.</summary>
    Unavailable,

    /// <summary>The installed version is current.</summary>
    Current,

    /// <summary>A newer stable release is available.</summary>
    UpdateAvailable,
}

/// <summary>Immutable release availability result.</summary>
/// <param name="Availability">Availability outcome.</param>
/// <param name="LatestVersion">Canonical latest version when an update is available; otherwise null.</param>
internal sealed record ApplicationUpdateCheckResult(
    ApplicationUpdateAvailability Availability,
    string? LatestVersion)
{
    /// <summary>Creates an unavailable result.</summary>
    public static ApplicationUpdateCheckResult Unavailable() =>
        new(ApplicationUpdateAvailability.Unavailable, null);

    /// <summary>Creates a current-version result.</summary>
    public static ApplicationUpdateCheckResult Current() =>
        new(ApplicationUpdateAvailability.Current, null);

    /// <summary>Creates an update-available result.</summary>
    /// <param name="latestVersion">Canonical latest stable version.</param>
    public static ApplicationUpdateCheckResult UpdateAvailable(string latestVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(latestVersion);
        return new(ApplicationUpdateAvailability.UpdateAvailable, latestVersion);
    }
}

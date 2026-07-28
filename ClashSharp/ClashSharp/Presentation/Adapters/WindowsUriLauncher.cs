using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ViewModel;
using Windows.System;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts Windows URI launching to the about page.</summary>
/// <remarks>
/// Invariants: Launch requests are delegated to the platform launcher.
/// Thread safety: Intended for UI-thread use.
/// Side effects: Opens an external URI through Windows.
/// </remarks>
internal sealed class WindowsUriLauncher : IUriLauncher
{
    /// <summary>Launches the supplied URI.</summary>
    /// <param name="uri">URI to launch. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token accepted for command consistency.</param>
    /// <returns>A task that completes after the platform launch request completes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
    /// <remarks>
    /// Cancellation semantics: The platform launcher does not expose cancellation; canceled tokens are ignored.
    /// Completion semantics: Completion does not guarantee the external application remains open.
    /// </remarks>
    public async Task LaunchAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await Launcher.LaunchUriAsync(uri);
    }
}

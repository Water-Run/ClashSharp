using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.ViewModel;

/// <summary>URI launcher contract used by <see cref="AboutViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations attempt to open the supplied URI.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: Opens an external URI.
/// </remarks>
internal interface IUriLauncher
{
    /// <summary>Launches the supplied URI.</summary>
    /// <param name="uri">URI to launch. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token accepted for command consistency.</param>
    /// <returns>A task that completes after the launch request has been submitted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
    /// <remarks>
    /// Cancellation semantics: Determined by the concrete implementation.
    /// Completion semantics: Completion does not guarantee the external application remains open.
    /// </remarks>
    Task LaunchAsync(Uri uri, CancellationToken cancellationToken);
}

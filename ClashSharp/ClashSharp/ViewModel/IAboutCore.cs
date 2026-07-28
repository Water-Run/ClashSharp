using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.ViewModel;

/// <summary>Core version contract used by <see cref="AboutViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations return a non-empty version string when the core is available.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May start a short-lived version probe process.
/// </remarks>
internal interface IAboutCore
{
    /// <summary>Gets bundled core version text.</summary>
    /// <param name="cancellationToken">Cancels the version probe when requested.</param>
    /// <returns>Version text.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the version probe.
    /// Completion semantics: Does not mutate long-running core state.
    /// </remarks>
    Task<string> GetVersionTextAsync(CancellationToken cancellationToken);
}

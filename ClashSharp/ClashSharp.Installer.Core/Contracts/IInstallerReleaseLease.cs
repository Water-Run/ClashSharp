using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Contracts;

/// <summary>
/// Owns the immutable, already-open release payload guards for one complete coordinator execution.
/// Disposing the lease releases every file and directory handle only after final verification.
/// </summary>
public interface IInstallerReleaseLease : IAsyncDisposable
{
    /// <summary>Gets the exact immutable release identity proven by the held payload handles.</summary>
    VerifiedInstallerRelease Release { get; }

    /// <summary>Gets the exact trust anchor embedded in the signed installer executable.</summary>
    InstallerReleaseManifest Manifest { get; }

    /// <summary>
    /// Gets the exact currently available file objects. Platform handles remain owned by this lease.
    /// </summary>
    IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles { get; }

    /// <summary>
    /// Rehashes the same open file objects and proves that every published path still names them.
    /// </summary>
    Task ReverifyAsync(
        InstallerRequest request,
        CancellationToken cancellationToken);
}

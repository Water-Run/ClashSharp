using System.Security.Principal;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Applies exact physical user-store operations only when the invoking token is the target user.
/// An OTS helper must use the separately authenticated target-SID adapter instead.
/// </summary>
public sealed class WindowsCurrentUserCertificateStoreAdapter : IInstallerCertificateStoreAdapter
{
    private readonly WindowsTargetUserCertificateStoreAdapter _targetUserStore = new();

    /// <inheritdoc />
    public Task<InstallerCertificatePresence> InspectAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        return _targetUserStore.InspectAsync(request, release, cancellationToken);
    }

    /// <inheritdoc />
    public Task ImportAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        return _targetUserStore.ImportAsync(request, release, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveExactAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        return _targetUserStore.RemoveExactAsync(request, release, cancellationToken);
    }

    private static void ValidateBoundary(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Release.Validate();
        release.Manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (release is not WindowsInstallerReleaseLease windowsLease
            || !release.Manifest.Matches(release.Release))
        {
            throw new InstallerProtocolException("installer.release.windows_lease_required");
        }

        windowsLease.RequireRequest(request);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (!string.Equals(identity.User?.Value, request.TargetSid, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.certificate.target_user_mismatch");
        }
    }
}

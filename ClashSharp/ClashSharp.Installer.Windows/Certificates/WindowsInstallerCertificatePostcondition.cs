using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Implements the coordinator's certificate port as an unelevated, read-only postcondition check.
/// The authenticated helper owns the protected ledger and every target-SID certificate mutation.
/// </summary>
public sealed class WindowsInstallerCertificatePostcondition : IInstallerCertificateMutation
{
    private readonly IInstallerCertificateStoreAdapter _certificateStore;

    /// <summary>Creates a verifier for the invoking target user's CurrentUser store.</summary>
    public WindowsInstallerCertificatePostcondition()
        : this(new WindowsCurrentUserCertificateStoreAdapter())
    {
    }

    internal WindowsInstallerCertificatePostcondition(
        IInstallerCertificateStoreAdapter certificateStore)
    {
        ArgumentNullException.ThrowIfNull(certificateStore);
        _certificateStore = certificateStore;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Release.Validate();
        InstallerCertificatePresence presence = await _certificateStore
            .InspectAsync(request, release, cancellationToken)
            .ConfigureAwait(false);
        bool valid = request.Operation switch
        {
            InstallerOperation.Install or InstallerOperation.Repair =>
                presence == InstallerCertificatePresence.ExactMatch,
            InstallerOperation.Uninstall => presence is
                InstallerCertificatePresence.Missing
                or InstallerCertificatePresence.ExactMatch,
            _ => false,
        };
        if (!Enum.IsDefined(presence) || !valid)
        {
            throw new InstallerProtocolException(
                "installer.certificate.postcondition_failed");
        }
    }
}

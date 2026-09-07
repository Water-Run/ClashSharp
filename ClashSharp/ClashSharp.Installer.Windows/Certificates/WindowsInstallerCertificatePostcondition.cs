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
    private readonly IInstallerCertificateStoreAdapter? _machineCertificateStore;

    /// <summary>Creates read-only verifiers for target-user trust and machine MSIX trust.</summary>
    public WindowsInstallerCertificatePostcondition()
        : this(new WindowsCurrentUserCertificateStoreAdapter(), new WindowsMachineCertificateStoreAdapter())
    {
    }

    internal WindowsInstallerCertificatePostcondition(
        IInstallerCertificateStoreAdapter certificateStore,
        IInstallerCertificateStoreAdapter? machineCertificateStore = null)
    {
        ArgumentNullException.ThrowIfNull(certificateStore);
        _certificateStore = certificateStore;
        _machineCertificateStore = machineCertificateStore;
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
        if (_machineCertificateStore is not null)
        {
            InstallerCertificatePresence machinePresence = await _machineCertificateStore
                .InspectAsync(request, release, cancellationToken).ConfigureAwait(false);
            bool machineValid = request.Operation == InstallerOperation.Uninstall
                ? machinePresence is InstallerCertificatePresence.Missing or InstallerCertificatePresence.ExactMatch
                : machinePresence == InstallerCertificatePresence.ExactMatch;
            if (!machineValid)
            {
                throw new InstallerProtocolException("installer.machine_certificate.postcondition_failed");
            }
        }
    }
}

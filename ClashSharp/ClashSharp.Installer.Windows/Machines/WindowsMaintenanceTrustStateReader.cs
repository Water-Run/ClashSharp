using System.Security.Cryptography;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMaintenanceTrustStateReader
{
    Task<WindowsMaintenanceTrustFingerprint> ReadAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken);
}

/// <summary>Reads both strict ownership documents and both exact physical trust postconditions.</summary>
internal sealed class WindowsMaintenanceTrustStateReader(IInstallerCertificateOwnershipStore userLedger,
    IInstallerMachineCertificatePersistence machineLedger, IInstallerCertificateStoreAdapter userStore,
    IInstallerCertificateStoreAdapter machineStore) : IWindowsMaintenanceTrustStateReader
{
    private readonly IInstallerCertificateOwnershipStore _userLedger = userLedger ?? throw new ArgumentNullException(nameof(userLedger));
    private readonly IInstallerMachineCertificatePersistence _machineLedger = machineLedger ?? throw new ArgumentNullException(nameof(machineLedger));
    private readonly IInstallerCertificateStoreAdapter _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
    private readonly IInstallerCertificateStoreAdapter _machineStore = machineStore ?? throw new ArgumentNullException(nameof(machineStore));

    public async Task<WindowsMaintenanceTrustFingerprint> ReadAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Manifest.Validate();
        release.Release.Validate();
        if (request.Operation != InstallerOperation.Repair || !release.Manifest.Matches(release.Release)
            || request.ExpectedPackageVersion != release.Release.ExpectedPackageVersion
            || request.InstallerPayloadSha256 != release.Release.InstallerPayloadSha256)
        {
            throw new InstallerProtocolException("installer.recovery.original_trust_mismatch");
        }
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        InstallerCertificateOwnershipSnapshot user = await _userLedger.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InstallerProtocolException("installer.recovery.original_trust_missing");
        user.Ledger.Validate();
        if (!user.Ledger.Matches(request, release.Release) || user.Ledger.ManagedReferenceCount != 1)
        {
            throw new InstallerProtocolException("installer.recovery.original_trust_mismatch");
        }
        byte[] userBytes = InstallerCertificateOwnershipCodec.Serialize(user.Ledger);
        byte[]? machineBytes = null;
        try
        {
            string userHash = Convert.ToHexStringLower(SHA256.HashData(userBytes));
            if (userHash != user.ContentHash) { throw new InstallerProtocolException("installer.recovery.original_trust_mismatch"); }
            machineBytes = await _machineLedger.ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerProtocolException("installer.recovery.original_trust_missing");
            InstallerMachineCertificateOwnership machine = InstallerMachineCertificateOwnership.Parse(machineBytes);
            machine.RequireManifest(release.Manifest);
            if (await _userStore.InspectAsync(request, release, cancellationToken).ConfigureAwait(false) != InstallerCertificatePresence.ExactMatch
                || await _machineStore.InspectAsync(request, release, cancellationToken).ConfigureAwait(false) != InstallerCertificatePresence.ExactMatch)
            {
                throw new InstallerProtocolException("installer.recovery.original_trust_changed");
            }
            await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            return new(userHash, Convert.ToHexStringLower(SHA256.HashData(machineBytes)), machine.CertificateThumbprint, machine.CertificateSha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(userBytes);
            if (machineBytes is not null) { CryptographicOperations.ZeroMemory(machineBytes); }
        }
    }
}

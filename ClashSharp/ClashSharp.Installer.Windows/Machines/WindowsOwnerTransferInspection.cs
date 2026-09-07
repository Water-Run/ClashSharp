using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Helper-only read-only discovery. A bounded association read is merely an untrusted SID hint:
/// both fixed machine roots and the exact association are independently verified before use.
/// Missing private roots stay missing, and no App, certificate or Windows service is modified.
/// </summary>
internal sealed class WindowsOwnerTransferInspection : IWindowsOwnerTransferInspection
{
    private readonly IWindowsOwnerTransferServiceBackend _backend;
    private readonly WindowsInstallerTransactionRootGuard _privateGuard;
    private bool _disposed;

    internal WindowsOwnerTransferInspection(IWindowsOwnerTransferServiceBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _privateGuard = WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault();
    }

    public async Task<InstallerOwnerTransferSnapshot?> LoadPrivateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _privateGuard.EnsureProtectedAsync(_privateGuard.RootPath, cancellationToken).ConfigureAwait(false);
        if (!_privateGuard.IsProtectedRootPresent)
        {
            return null;
        }
        byte[]? bytes = await WindowsInstallerPrivateJournalFileNative.Instance.ReadAsync(
            Path.Combine(_privateGuard.RootPath, InstallerOwnerTransferStateLayout.JournalFileName), cancellationToken).ConfigureAwait(false);
        try
        {
            return bytes is null ? null : InstallerOwnerTransferSnapshot.Create(InstallerOwnerTransferCodec.Parse(bytes));
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    public async Task<InstallerOwnerTransferJournal> CaptureNewAsync(
        InstallerRequest request, InstallerReleaseManifest manifest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        request.Validate();
        manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        InstallerMachineAssociation previous = DiscoverAssociationHint();
        if (previous.OwnerSid == request.TargetSid)
        {
            throw new InstallerProtocolException("installer.owner_transfer.same_owner");
        }
        string previousProfile = _backend.ResolveTargetProfile(previous.OwnerSid, cancellationToken);
        string nextProfile = _backend.ResolveTargetProfile(request.TargetSid, cancellationToken);
        var previousRequest = request with { Operation = InstallerOperation.Uninstall, TargetSid = previous.OwnerSid };
        WindowsMachineDeploymentPlan previousPlan = _backend.CreatePlan(previousRequest, manifest, previous, previousProfile, removalPlan: true);
        using IWindowsMachineRootGuard machine = _backend.CreateRootGuard(previousPlan, createMissing: false);
        using IWindowsMachineAssociationStore association = _backend.CreateAssociationStore(previousPlan, machine);
        await association.VerifyExactAsync(cancellationToken).ConfigureAwait(false);
        using WindowsInstallerTransactionRootGuard ordinaryGuard = WindowsInstallerTransactionRootGuard.CreateReadOnlyDefault(previous.OwnerSid);
        using var ordinary = new FileInstallerTransactionStore(ordinaryGuard.RootPath, ordinaryGuard);
        if (await ordinary.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InstallerProtocolException("installer.owner_transfer.ordinary_state_pending");
        }
        // Generate these identities once inside the authenticated helper. The parent can neither
        // supply replacements nor select private archive paths.
        var next = InstallerMachineAssociation.Create(request.TargetSid, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournal.Create(
            request, new(previous, previousProfile), new(next, nextProfile));
        var plan = new WindowsOwnerTransferCertificatePlan(previousPlan.Roots, journal);
        await _privateGuard.EnsureProtectedAsync(_privateGuard.RootPath, cancellationToken).ConfigureAwait(false);
        InstallerOwnerTransferCertificateState certificates = await new WindowsOwnerTransferCertificateFileNative()
            .ReadForInspectionAsync(plan, _privateGuard.IsProtectedRootPresent, cancellationToken).ConfigureAwait(false);
        if (certificates.PreviousArchive is not null)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_state_conflict");
        }
        journal = journal with { PreviousCertificateLedger = certificates.Active, NextCertificateLedger = certificates.NextArchive };
        journal.Validate();
        await association.VerifyExactAsync(cancellationToken).ConfigureAwait(false);
        await ordinaryGuard.EnsureProtectedAsync(ordinaryGuard.RootPath, cancellationToken).ConfigureAwait(false);
        await _privateGuard.EnsureProtectedAsync(_privateGuard.RootPath, cancellationToken).ConfigureAwait(false);
        return journal;
    }

    private static InstallerMachineAssociation DiscoverAssociationHint()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        WindowsMachineAssociationFileObservation observation = WindowsMachineAssociationFileNative.Instance.Read(
            Path.Combine(programData, InstallerStateLayout.ProductDirectoryName, "MihomoService", "association.json"));
        try
        {
            observation.Validate();
            if (observation.Status != WindowsMachineAssociationFileStatus.OrdinaryFile)
            {
                throw new InstallerProtocolException("installer.owner_transfer.previous_association_missing");
            }
            return InstallerMachineAssociationCodec.Parse(observation.Bytes!);
        }
        catch (InstallerProtocolException)
        {
            throw new InstallerProtocolException("installer.owner_transfer.previous_association_invalid");
        }
        finally
        {
            if (observation.Bytes is { } bytes)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _privateGuard.Dispose();
        }
    }
}

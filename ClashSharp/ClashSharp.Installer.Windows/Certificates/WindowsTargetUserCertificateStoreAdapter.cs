using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Applies certificate operations to the exact target user's TrustedPeople store from the
/// authenticated elevated helper. This type is intentionally internal so parent-side composition
/// cannot accidentally substitute an over-the-shoulder administrator's CurrentUser store.
/// </summary>
internal sealed class WindowsTargetUserCertificateStoreAdapter
    : IInstallerCertificateStoreAdapter
{
    private readonly IWindowsTargetUserCertificateStoreNative _native;

    internal WindowsTargetUserCertificateStoreAdapter()
        : this(WindowsTargetUserCertificateStoreNative.Instance)
    {
    }

    internal WindowsTargetUserCertificateStoreAdapter(
        IWindowsTargetUserCertificateStoreNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public Task<InstallerCertificatePresence> InspectAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        try
        {
            using IWindowsTargetUserCertificateStore? store = _native.Open(
                request.TargetSid,
                writable: false,
                createIfMissing: false);
            if (store is null)
            {
                return Task.FromResult(InstallerCertificatePresence.Missing);
            }

            return Task.FromResult(InspectStore(store, release.Release, cancellationToken));
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.certificate.inspection_failed",
                exception);
        }
    }

    public Task ImportAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        WindowsInstallerReleaseLease windowsLease = ValidateBoundary(
            request,
            release,
            cancellationToken);
        WindowsLockedPayloadFile certificateFile = windowsLease.RequireFile(
            InstallerPayloadFileRole.Certificate);
        byte[] bytes = certificateFile.ReadAllBytes(cancellationToken);
        try
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(bytes);
            ValidateExactCertificate(certificate, release.Release);
            if (certificate.HasPrivateKey)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.private_key_rejected");
            }

            using IWindowsTargetUserCertificateStore store = _native.Open(
                request.TargetSid,
                writable: true,
                createIfMissing: true)
                ?? throw new InstallerProtocolException(
                    "installer.certificate.store_creation_failed");
            InstallerCertificatePresence presence = InspectStore(
                store,
                release.Release,
                cancellationToken);
            ThrowIfConflict(presence);
            if (presence == InstallerCertificatePresence.ExactMatch)
            {
                return Task.CompletedTask;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                store.AddEncodedCertificate(bytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // CERT_STORE_ADD_NEW can lose a benign race to another exact importer. Re-read
                // through the same fixed store before classifying the result as a failure.
                InstallerCertificatePresence racedPresence = InspectStore(
                    store,
                    release.Release,
                    cancellationToken);
                ThrowIfConflict(racedPresence);
                if (racedPresence != InstallerCertificatePresence.ExactMatch)
                {
                    throw new InstallerProtocolException(
                        "installer.certificate.import_failed",
                        exception);
                }

                return Task.CompletedTask;
            }

            if (InspectStore(store, release.Release, cancellationToken)
                != InstallerCertificatePresence.ExactMatch)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.import_verification_failed");
            }

            return Task.CompletedTask;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.certificate.import_failed",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public Task RemoveExactAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        try
        {
            using IWindowsTargetUserCertificateStore? store = _native.Open(
                request.TargetSid,
                writable: true,
                createIfMissing: false);
            if (store is null)
            {
                return Task.CompletedTask;
            }

            InstallerCertificatePresence presence = InspectStore(
                store,
                release.Release,
                cancellationToken);
            ThrowIfConflict(presence);
            if (presence == InstallerCertificatePresence.Missing)
            {
                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            store.DeleteExactCertificates(
                release.Release.PackageCertificateThumbprint,
                release.Release.CertificateSha256,
                cancellationToken);
            InstallerCertificatePresence remaining = InspectStore(
                store,
                release.Release,
                cancellationToken);
            ThrowIfConflict(remaining);
            if (remaining != InstallerCertificatePresence.Missing)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.removal_verification_failed");
            }

            return Task.CompletedTask;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.certificate.removal_failed",
                exception);
        }
    }

    private static WindowsInstallerReleaseLease ValidateBoundary(
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            throw new InstallerProtocolException(
                "installer.certificate.platform_unsupported");
        }

        if (release is not WindowsInstallerReleaseLease windowsLease
            || !release.Manifest.Matches(release.Release))
        {
            throw new InstallerProtocolException(
                "installer.release.windows_lease_required");
        }

        windowsLease.RequireRequest(request);
        return windowsLease;
    }

    private static InstallerCertificatePresence InspectStore(
        IWindowsTargetUserCertificateStore store,
        VerifiedInstallerRelease release,
        CancellationToken cancellationToken)
    {
        bool exact = false;
        bool conflict = false;
        foreach (WindowsCertificateIdentity identity in
                 store.EnumerateCertificateIdentities(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    identity.Thumbprint,
                    release.PackageCertificateThumbprint,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(
                    identity.DerSha256,
                    release.CertificateSha256,
                    StringComparison.Ordinal))
            {
                exact = true;
            }
            else
            {
                conflict = true;
            }
        }

        return conflict
            ? InstallerCertificatePresence.IdentityConflict
            : exact
                ? InstallerCertificatePresence.ExactMatch
                : InstallerCertificatePresence.Missing;
    }

    private static void ValidateExactCertificate(
        X509Certificate2 certificate,
        VerifiedInstallerRelease release)
    {
        WindowsCertificateIdentity identity = WindowsCertificateIdentity.FromEncoded(
            certificate.RawData);
        if (!identity.Matches(
                release.PackageCertificateThumbprint,
                release.CertificateSha256))
        {
            throw new InstallerProtocolException(
                "installer.certificate.payload_identity_invalid");
        }
    }

    private static void ThrowIfConflict(InstallerCertificatePresence presence)
    {
        if (presence == InstallerCertificatePresence.IdentityConflict)
        {
            throw new InstallerProtocolException(
                "installer.certificate.identity_conflict");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

internal readonly record struct WindowsCertificateIdentity(
    string Thumbprint,
    string DerSha256)
{
    internal static WindowsCertificateIdentity FromEncoded(ReadOnlySpan<byte> encoded)
    {
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(encoded);
        return new(
            Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA1)),
            Convert.ToHexStringLower(SHA256.HashData(encoded)));
    }

    internal bool Matches(string thumbprint, string derSha256) =>
        string.Equals(Thumbprint, thumbprint, StringComparison.Ordinal)
        && string.Equals(DerSha256, derSha256, StringComparison.Ordinal);
}

internal interface IWindowsTargetUserCertificateStoreNative
{
    IWindowsTargetUserCertificateStore? Open(
        string targetSid,
        bool writable,
        bool createIfMissing);
}

internal interface IWindowsTargetUserCertificateStore : IDisposable
{
    IReadOnlyList<WindowsCertificateIdentity> EnumerateCertificateIdentities(
        CancellationToken cancellationToken);

    void AddEncodedCertificate(byte[] encodedCertificate);

    int DeleteExactCertificates(
        string expectedThumbprint,
        string expectedDerSha256,
        CancellationToken cancellationToken);
}

internal sealed class WindowsTargetUserCertificateStoreNative
    : IWindowsTargetUserCertificateStoreNative
{
    private const nint CertificateStoreProviderSystemWide = 10;
    private const uint CertificateSystemStoreUsers = 0x0006_0000;
    private const uint CertificateStoreOpenExisting = 0x0000_4000;
    private const uint CertificateStoreReadOnly = 0x0000_8000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int CryptENotFound = unchecked((int)0x8009_2004);

    internal static WindowsTargetUserCertificateStoreNative Instance { get; } = new();

    private WindowsTargetUserCertificateStoreNative()
    {
    }

    public IWindowsTargetUserCertificateStore? Open(
        string targetSid,
        bool writable,
        bool createIfMissing)
    {
        string systemStoreName = BuildSystemStoreName(targetSid);
        uint flags = BuildOpenFlags(writable, createIfMissing);

        SafeWindowsCertificateStoreHandle handle = CertOpenStore(
            CertificateStoreProviderSystemWide,
            encodingType: 0,
            cryptographicProvider: 0,
            flags,
            systemStoreName);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (!createIfMissing && IsStoreMissing(error))
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        return new WindowsTargetUserCertificateStore(handle, writable);
    }

    internal static string BuildSystemStoreName(string targetSid)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        return $"{targetSid}\\TrustedPeople";
    }

    internal static uint BuildOpenFlags(bool writable, bool createIfMissing)
    {
        if (!writable && createIfMissing)
        {
            throw new InstallerProtocolException(
                "installer.certificate.store_open_mode_invalid");
        }

        uint flags = CertificateSystemStoreUsers;
        if (!createIfMissing)
        {
            flags |= CertificateStoreOpenExisting;
        }

        if (!writable)
        {
            flags |= CertificateStoreReadOnly;
        }

        return flags;
    }

    private static bool IsStoreMissing(int error) =>
        error is ErrorFileNotFound or ErrorPathNotFound or CryptENotFound;

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWindowsCertificateStoreHandle CertOpenStore(
        nint storeProvider,
        uint encodingType,
        nint cryptographicProvider,
        uint flags,
        string parameter);
}

internal sealed class WindowsTargetUserCertificateStore
    : IWindowsTargetUserCertificateStore
{
    private const uint X509AsnEncoding = 0x0000_0001;
    private const uint CertificateStoreAddNew = 1;
    private const int CryptENotFound = unchecked((int)0x8009_2004);
    private const int ErrorNoMoreFiles = 18;
    private const int MaximumCertificateCount = 16_384;
    private const int MaximumCertificateBytes = 1024 * 1024;
    private const int MaximumEnumerationSteps = 65_536;

    private readonly bool _writable;
    private SafeWindowsCertificateStoreHandle? _handle;

    internal WindowsTargetUserCertificateStore(
        SafeWindowsCertificateStoreHandle handle,
        bool writable)
    {
        _handle = handle;
        _writable = writable;
    }

    public IReadOnlyList<WindowsCertificateIdentity> EnumerateCertificateIdentities(
        CancellationToken cancellationToken)
    {
        SafeWindowsCertificateStoreHandle handle = RequireHandle();
        var identities = new List<WindowsCertificateIdentity>();
        nint current = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint next = CertEnumCertificatesInStore(handle, current);
                current = 0;
                if (next == 0)
                {
                    ThrowUnlessEnumerationComplete();
                    return identities;
                }

                current = next;
                if (identities.Count >= MaximumCertificateCount)
                {
                    throw new InvalidDataException(
                        "The target certificate store exceeds the supported bound.");
                }

                identities.Add(WindowsCertificateIdentity.FromEncoded(
                    CopyEncodedCertificate(current)));
            }
        }
        finally
        {
            if (current != 0)
            {
                _ = CertFreeCertificateContext(current);
            }
        }
    }

    public void AddEncodedCertificate(byte[] encodedCertificate)
    {
        ArgumentNullException.ThrowIfNull(encodedCertificate);
        EnsureWritable();
        if (encodedCertificate.Length is < 1 or > MaximumCertificateBytes)
        {
            throw new InvalidDataException(
                "The encoded certificate exceeds the supported bound.");
        }

        if (!CertAddEncodedCertificateToStore(
                RequireHandle(),
                X509AsnEncoding,
                encodedCertificate,
                checked((uint)encodedCertificate.Length),
                CertificateStoreAddNew,
                addedCertificate: 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public int DeleteExactCertificates(
        string expectedThumbprint,
        string expectedDerSha256,
        CancellationToken cancellationToken)
    {
        InstallerProtocolValidation.ValidateUpperHex160(
            expectedThumbprint,
            "installer.certificate.thumbprint_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(
            expectedDerSha256,
            "installer.certificate.der_hash_invalid");
        EnsureWritable();

        SafeWindowsCertificateStoreHandle handle = RequireHandle();
        int deleted = 0;
        int steps = 0;
        nint current = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++steps > MaximumEnumerationSteps)
                {
                    throw new InvalidDataException(
                        "The target certificate store changed beyond the supported bound.");
                }

                nint next = CertEnumCertificatesInStore(handle, current);
                current = 0;
                if (next == 0)
                {
                    ThrowUnlessEnumerationComplete();
                    return deleted;
                }

                current = next;
                WindowsCertificateIdentity identity = WindowsCertificateIdentity.FromEncoded(
                    CopyEncodedCertificate(current));
                if (!identity.Matches(expectedThumbprint, expectedDerSha256))
                {
                    continue;
                }

                nint deleting = current;
                current = 0;
                // CertDeleteCertificateFromStore consumes the context even when deletion fails.
                if (!CertDeleteCertificateFromStore(deleting))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                deleted = checked(deleted + 1);
            }
        }
        finally
        {
            if (current != 0)
            {
                _ = CertFreeCertificateContext(current);
            }
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private SafeWindowsCertificateStoreHandle RequireHandle() =>
        _handle ?? throw new ObjectDisposedException(GetType().FullName);

    private void EnsureWritable()
    {
        if (!_writable)
        {
            throw new InvalidOperationException(
                "The target certificate store was opened read-only.");
        }
    }

    private static byte[] CopyEncodedCertificate(nint certificateContext)
    {
        CertificateContext context = Marshal.PtrToStructure<CertificateContext>(
            certificateContext);
        if (context.EncodedCertificate == 0
            || context.EncodedCertificateBytes is 0 or > MaximumCertificateBytes)
        {
            throw new InvalidDataException(
                "The certificate store returned an invalid encoded certificate.");
        }

        byte[] encoded = GC.AllocateUninitializedArray<byte>(
            checked((int)context.EncodedCertificateBytes));
        Marshal.Copy(context.EncodedCertificate, encoded, startIndex: 0, encoded.Length);
        return encoded;
    }

    private static void ThrowUnlessEnumerationComplete()
    {
        int error = Marshal.GetLastPInvokeError();
        if (error is not (CryptENotFound or ErrorNoMoreFiles))
        {
            throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint EncodingType;
        internal nint EncodedCertificate;
        internal uint EncodedCertificateBytes;
        internal nint CertificateInfo;
        internal nint CertificateStore;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CertEnumCertificatesInStore(
        SafeWindowsCertificateStoreHandle certificateStore,
        nint previousCertificateContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertAddEncodedCertificateToStore(
        SafeWindowsCertificateStoreHandle certificateStore,
        uint certificateEncodingType,
        byte[] encodedCertificate,
        uint encodedCertificateBytes,
        uint addDisposition,
        nint addedCertificate);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertDeleteCertificateFromStore(nint certificateContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertFreeCertificateContext(nint certificateContext);
}

internal sealed class SafeWindowsCertificateStoreHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeWindowsCertificateStoreHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => CertCloseStore(handle, flags: 0);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCloseStore(nint certificateStore, uint flags);
}

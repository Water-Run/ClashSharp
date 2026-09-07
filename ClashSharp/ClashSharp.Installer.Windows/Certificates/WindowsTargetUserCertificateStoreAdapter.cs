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

/// <summary>Mutates only the authenticated target user's physical TrustedPeople registry store.</summary>
internal sealed class WindowsTargetUserCertificateStoreAdapter : WindowsCertificateStoreAdapter
{
    internal WindowsTargetUserCertificateStoreAdapter()
        : this(WindowsTargetUserCertificateStoreNative.Instance)
    {
    }

    internal WindowsTargetUserCertificateStoreAdapter(IWindowsCertificateStoreNative native)
        : base(native)
    {
    }
}

internal readonly record struct WindowsCertificateIdentity(
    string Thumbprint,
    string DerSha256)
{
    internal static InstallerCertificatePresence InspectStore(IWindowsCertificateStore store,
        string thumbprint, string derSha256, CancellationToken cancellationToken)
    {
        bool exact = false;
        bool conflict = false;
        foreach (WindowsCertificateIdentity identity in store.EnumerateCertificateIdentities(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity.Thumbprint != thumbprint)
            {
                continue;
            }
            if (identity.DerSha256 == derSha256)
            {
                exact = true;
            }
            else
            {
                conflict = true;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return conflict ? InstallerCertificatePresence.IdentityConflict
            : exact ? InstallerCertificatePresence.ExactMatch : InstallerCertificatePresence.Missing;
    }

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

internal interface IWindowsCertificateStoreNative
{
    IWindowsCertificateStore? Open(
        string targetSid,
        bool writable,
        bool createIfMissing);
}

internal interface IWindowsCertificateStore : IDisposable
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
    : IWindowsCertificateStoreNative
{
    // A logical system store also exposes machine/group-policy siblings. Exact user ownership
    // authorizes only the physical registry store, never deletion through an inherited context.
    private const nint CertificateStoreProviderSystemRegistryWide = 13;
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

    public IWindowsCertificateStore? Open(
        string targetSid,
        bool writable,
        bool createIfMissing)
    {
        string systemStoreName = BuildSystemStoreName(targetSid);
        uint flags = BuildOpenFlags(writable, createIfMissing);

        SafeWindowsCertificateStoreHandle handle = CertOpenStore(
            CertificateStoreProviderSystemRegistryWide,
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

        return new WindowsCertificateStore(handle, writable);
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

internal sealed class WindowsCertificateStore
    : IWindowsCertificateStore
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

    internal WindowsCertificateStore(
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

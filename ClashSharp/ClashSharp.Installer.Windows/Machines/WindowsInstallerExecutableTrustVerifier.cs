using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsInstallerExecutableTrustLease : IDisposable
{
    string ExecutablePath { get; }
}

internal interface IWindowsAuthenticodeVerifier
{
    string VerifyTrustedSigner(
        string executablePath,
        SafeFileHandle lockedExecutableHandle,
        CancellationToken cancellationToken);
}

/// <summary>
/// Locks the exact single-file Installer against write/rename, verifies Authenticode trust on that
/// handle, and pins the expected release signer for the complete elevated-helper lifetime.
/// </summary>
internal sealed class WindowsInstallerExecutableTrustVerifier
    : IWindowsInstallerExecutableTrustVerifier
{
    private readonly string _expectedSignerThumbprint;
    private readonly IWindowsAuthenticodeVerifier _authenticode;

    internal WindowsInstallerExecutableTrustVerifier(string expectedSignerThumbprint)
        : this(expectedSignerThumbprint, WindowsAuthenticodeVerifier.Instance)
    {
    }

    internal WindowsInstallerExecutableTrustVerifier(InstallerReleaseManifest manifest)
        : this(RequireSignerThumbprint(manifest), WindowsAuthenticodeVerifier.Instance)
    {
    }

    internal WindowsInstallerExecutableTrustVerifier(
        InstallerReleaseManifest manifest,
        IWindowsAuthenticodeVerifier authenticode)
        : this(RequireSignerThumbprint(manifest), authenticode)
    {
    }

    internal WindowsInstallerExecutableTrustVerifier(
        string expectedSignerThumbprint,
        IWindowsAuthenticodeVerifier authenticode)
    {
        InstallerProtocolValidation.ValidateUpperHex160(
            expectedSignerThumbprint,
            "installer.elevation.signer_thumbprint_invalid");
        ArgumentNullException.ThrowIfNull(authenticode);
        _expectedSignerThumbprint = expectedSignerThumbprint;
        _authenticode = authenticode;
    }

    public Task<IWindowsInstallerExecutableTrustLease> VerifyAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = ValidateExecutablePath(executablePath);
        SafeFileHandle handle;
        try
        {
            handle = WindowsFileSystemNative.OpenOrdinaryFile(fullPath);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw new InstallerProtocolException(
                "installer.elevation.executable_lock_failed",
                exception);
        }

        try
        {
            WindowsFileIdentity lockedIdentity =
                WindowsFileSystemNative.GetOrdinaryFileIdentity(handle);
            string signerThumbprint = _authenticode.VerifyTrustedSigner(
                fullPath,
                handle,
                cancellationToken);
            InstallerProtocolValidation.ValidateUpperHex160(
                signerThumbprint,
                "installer.elevation.signer_thumbprint_invalid");
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(signerThumbprint),
                    Convert.FromHexString(_expectedSignerThumbprint)))
            {
                throw new InstallerProtocolException(
                    "installer.elevation.signer_mismatch");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using SafeFileHandle pathProbe = WindowsFileSystemNative.OpenOrdinaryFile(fullPath);
            if (WindowsFileSystemNative.GetOrdinaryFileIdentity(pathProbe) != lockedIdentity)
            {
                throw new InstallerProtocolException(
                    "installer.elevation.executable_identity_changed");
            }

            IWindowsInstallerExecutableTrustLease lease =
                new WindowsInstallerExecutableTrustLease(fullPath, handle, lockedIdentity);
            handle = null!;
            return Task.FromResult(lease);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw new InstallerProtocolException(
                "installer.elevation.executable_verification_failed",
                exception);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new InstallerProtocolException(
                "installer.elevation.executable_path_invalid");
        }

        string fullPath = Path.GetFullPath(executablePath);
        if (fullPath.Length < 3
            || !char.IsAsciiLetter(fullPath[0])
            || fullPath[1] != ':'
            || fullPath[2] != Path.DirectorySeparatorChar
            || !string.Equals(
                Path.GetFileName(fullPath),
                "ClashSharp.Installer.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.elevation.executable_path_invalid");
        }

        return fullPath;
    }

    private static string RequireSignerThumbprint(InstallerReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        return manifest.AuthenticodeCertificateThumbprint;
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or CryptographicException;
}

internal sealed class WindowsInstallerExecutableTrustLease
    : IWindowsInstallerExecutableTrustLease
{
    private readonly WindowsFileIdentity _identity;
    private SafeFileHandle? _handle;

    internal WindowsInstallerExecutableTrustLease(
        string executablePath,
        SafeFileHandle handle,
        WindowsFileIdentity identity)
    {
        ExecutablePath = executablePath;
        _handle = handle;
        _identity = identity;
    }

    public string ExecutablePath { get; }

    internal WindowsFileIdentity Identity => _identity;

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

internal sealed class WindowsAuthenticodeVerifier : IWindowsAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2;
    private const uint ChoiceFile = 1;
    private const uint CacheOnlyUrlRetrieval = 0x0000_1000;

    internal static WindowsAuthenticodeVerifier Instance { get; } = new();

    private WindowsAuthenticodeVerifier()
    {
    }

    public string VerifyTrustedSigner(
        string executablePath,
        SafeFileHandle lockedExecutableHandle,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(lockedExecutableHandle);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Authenticode verification is available only on Windows.");
        }

        nint path = 0;
        nint fileInfoPointer = 0;
        Guid policy = GenericVerifyV2;
        WinTrustData trustData = default;
        try
        {
            path = Marshal.StringToCoTaskMemUni(executablePath);
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = path,
                FileHandle = lockedExecutableHandle.DangerousGetHandle(),
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            trustData = new WinTrustData
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = UiNone,
                UnionChoice = ChoiceFile,
                FileInfo = fileInfoPointer,
                ProviderFlags = CacheOnlyUrlRetrieval,
            };
            int status = WinVerifyTrust(new nint(-1), ref policy, ref trustData);
            if (status != 0)
            {
                throw new InstallerProtocolException(
                    "installer.elevation.authenticode_invalid");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ReadSignerThumbprint(trustData.StateData);
        }
        finally
        {
            if (trustData.StateData != 0)
            {
                trustData.StateAction = 2;
                _ = WinVerifyTrust(new nint(-1), ref policy, ref trustData);
            }

            if (fileInfoPointer != 0)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (path != 0)
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
    }

    private static string ReadSignerThumbprint(nint stateData)
    {
        nint providerData = WTHelperProvDataFromStateData(stateData);
        nint providerSigner = providerData == 0
            ? 0
            : WTHelperGetProvSignerFromChain(
                providerData,
                signerIndex: 0,
                counterSigner: false,
                counterSignerIndex: 0);
        if (providerSigner == 0)
        {
            throw new InstallerProtocolException(
                "installer.elevation.authenticode_signer_missing");
        }

        CryptProviderSigner signer = Marshal.PtrToStructure<CryptProviderSigner>(
            providerSigner);
        if (signer.CertificateChainCount == 0 || signer.CertificateChain == 0)
        {
            throw new InstallerProtocolException(
                "installer.elevation.authenticode_signer_missing");
        }

        CryptProviderCertificate providerCertificate =
            Marshal.PtrToStructure<CryptProviderCertificate>(signer.CertificateChain);
        if (providerCertificate.CertificateContext == 0)
        {
            throw new InstallerProtocolException(
                "installer.elevation.authenticode_signer_missing");
        }

        CertificateContext certificateContext = Marshal.PtrToStructure<CertificateContext>(
            providerCertificate.CertificateContext);
        if (certificateContext.EncodedCertificate == 0
            || certificateContext.EncodedCertificateLength == 0
            || certificateContext.EncodedCertificateLength > 1024 * 1024)
        {
            throw new InstallerProtocolException(
                "installer.elevation.authenticode_signer_invalid");
        }

        byte[] encoded = GC.AllocateUninitializedArray<byte>(
            checked((int)certificateContext.EncodedCertificateLength));
        Marshal.Copy(certificateContext.EncodedCertificate, encoded, 0, encoded.Length);
        try
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(encoded);
            return Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA1));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(
        nint windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint StructureSize;
        internal nint FilePath;
        internal nint FileHandle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructureSize;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint FileInfo;
        internal uint StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        internal uint StructureSize;
        internal FileTime VerifyAsOf;
        internal uint CertificateChainCount;
        internal nint CertificateChain;
        internal uint SignerType;
        internal nint Signer;
        internal uint Error;
        internal uint CounterSignerCount;
        internal nint CounterSigners;
        internal nint ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        internal uint StructureSize;
        internal nint CertificateContext;
        internal int Commercial;
        internal int TrustedRoot;
        internal int SelfSigned;
        internal int TestCertificate;
        internal uint RevokedReason;
        internal uint Confidence;
        internal uint Error;
        internal nint TrustListContext;
        internal int TrustListSignerCertificate;
        internal nint CertificateTrustListContext;
        internal uint CertificateTrustListError;
        internal int Cyclic;
        internal nint ChainElement;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint EncodingType;
        internal nint EncodedCertificate;
        internal uint EncodedCertificateLength;
        internal nint CertificateInfo;
        internal nint CertificateStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }
}

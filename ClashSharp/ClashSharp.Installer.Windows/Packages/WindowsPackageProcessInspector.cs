using System.Diagnostics;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Packages;

internal enum WindowsPackageProcessObservationKind
{
    Unpackaged,
    Packaged,
    Uncertain,
}

internal sealed record WindowsPackageProcessObservation(
    WindowsPackageProcessObservationKind Kind,
    string? PackageFamilyName);

internal interface IWindowsPackageProcessCatalog
{
    IReadOnlyList<WindowsPackageProcessObservation> ObserveCandidates(
        string executableBaseName,
        CancellationToken cancellationToken);
}

internal interface IWindowsPackageProcessInspector
{
    bool IsApplicationRunning(
        InstallerReleaseManifest manifest,
        CancellationToken cancellationToken);
}

/// <summary>
/// Distinguishes the exact packaged application from unrelated processes that merely share its
/// executable name. An uninspectable candidate is treated as running so mutation stays blocked.
/// </summary>
internal sealed class WindowsPackageProcessInspector : IWindowsPackageProcessInspector
{
    private readonly IWindowsPackageProcessCatalog _catalog;

    internal WindowsPackageProcessInspector()
        : this(WindowsPackageProcessCatalog.Instance)
    {
    }

    internal WindowsPackageProcessInspector(IWindowsPackageProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public bool IsApplicationRunning(
        InstallerReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        string executableBaseName = Path.GetFileNameWithoutExtension(
            manifest.PackageIdentity.ApplicationExecutable);
        IReadOnlyList<WindowsPackageProcessObservation> observations =
            _catalog.ObserveCandidates(executableBaseName, cancellationToken)
            ?? throw new InstallerProtocolException(
                "installer.application_process_inspection_failed");

        foreach (WindowsPackageProcessObservation? observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observation is null
                || !Enum.IsDefined(observation.Kind)
                || (observation.Kind == WindowsPackageProcessObservationKind.Packaged
                    && string.IsNullOrWhiteSpace(observation.PackageFamilyName)))
            {
                throw new InstallerProtocolException(
                    "installer.application_process_inspection_failed");
            }

            if (observation.Kind == WindowsPackageProcessObservationKind.Uncertain
                || (observation.Kind == WindowsPackageProcessObservationKind.Packaged
                    && string.Equals(
                        observation.PackageFamilyName,
                        manifest.PackageIdentity.PackageFamilyName,
                        StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class WindowsPackageProcessCatalog : IWindowsPackageProcessCatalog
{
    private const uint ProcessQueryLimitedInformation = 0x0000_1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15_700;

    internal static WindowsPackageProcessCatalog Instance { get; } = new();

    private WindowsPackageProcessCatalog()
    {
    }

    public IReadOnlyList<WindowsPackageProcessObservation> ObserveCandidates(
        string executableBaseName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executableBaseName)
            || executableBaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InstallerProtocolException(
                "installer.application_executable_invalid");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(executableBaseName);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.application_process_inspection_failed",
                exception);
        }

        try
        {
            var observations = new List<WindowsPackageProcessObservation>(processes.Length);
            foreach (Process process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observations.Add(ObserveProcess(process));
            }

            return observations;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static WindowsPackageProcessObservation ObserveProcess(Process process)
    {
        int processId;
        try
        {
            processId = process.Id;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Uncertain();
        }

        using SafeProcessHandle handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (handle.IsInvalid)
        {
            return Uncertain();
        }

        uint length = 0;
        int status = GetPackageFamilyName(handle, ref length, packageFamilyName: null);
        if (status == AppModelErrorNoPackage)
        {
            return new(
                WindowsPackageProcessObservationKind.Unpackaged,
                PackageFamilyName: null);
        }

        if (status != ErrorInsufficientBuffer || length is < 2 or > 256)
        {
            return Uncertain();
        }

        char[] familyName = GC.AllocateUninitializedArray<char>(checked((int)length));
        status = GetPackageFamilyName(handle, ref length, familyName);
        int textLength = Array.IndexOf(familyName, '\0');
        if (textLength < 0)
        {
            textLength = familyName.Length;
        }

        string observedFamily = new(familyName, 0, textLength);
        return status == 0 && !string.IsNullOrWhiteSpace(observedFamily)
            ? new(
                WindowsPackageProcessObservationKind.Packaged,
                observedFamily)
            : Uncertain();
    }

    private static WindowsPackageProcessObservation Uncertain() => new(
        WindowsPackageProcessObservationKind.Uncertain,
        PackageFamilyName: null);

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackageFamilyName(
        SafeProcessHandle process,
        ref uint packageFamilyNameLength,
        [Out] char[]? packageFamilyName);
}

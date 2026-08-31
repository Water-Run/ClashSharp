using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Platform;

namespace ClashSharp.Installer.Windows.Platform;

/// <summary>Captures manifest-independent Windows version and native architecture facts.</summary>
public sealed class WindowsInstallerPlatformProbe : IInstallerPlatformProbe
{
    private const byte WorkstationProductType = 1;
    private const ushort ProcessorArchitectureIntel = 0;
    private const ushort ProcessorArchitectureArm = 5;
    private const ushort ProcessorArchitectureAmd64 = 9;
    private const ushort ProcessorArchitectureArm64 = 12;

    /// <inheritdoc />
    public InstallerPlatformFacts Inspect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return new InstallerPlatformFacts(
                IsWindows: false,
                IsWorkstation: false,
                BuildNumber: 0,
                OperatingSystemArchitecture: InstallerCpuArchitecture.Unknown,
                ProcessArchitecture: InstallerCpuArchitecture.Unknown);
        }

        var version = new RtlOsVersionInfoEx
        {
            Size = checked((uint)Marshal.SizeOf<RtlOsVersionInfoEx>()),
            ServicePack = string.Empty,
        };
        int status = RtlGetVersion(ref version);
        if (status != 0 || version.BuildNumber > int.MaxValue)
        {
            throw new InstallerProtocolException("installer.environment.version_probe_failed");
        }

        GetNativeSystemInfo(out NativeSystemInfo systemInfo);
        InstallerCpuArchitecture processArchitecture = MapProcessArchitecture(
            RuntimeInformation.ProcessArchitecture);

        return new InstallerPlatformFacts(
            IsWindows: true,
            IsWorkstation: version.ProductType == WorkstationProductType,
            BuildNumber: checked((int)version.BuildNumber),
            OperatingSystemArchitecture: MapNativeArchitecture(
                systemInfo.ProcessorInfo.ProcessorArchitecture),
            ProcessArchitecture: processArchitecture);
    }

    private static InstallerCpuArchitecture MapNativeArchitecture(ushort architecture) =>
        architecture switch
        {
            ProcessorArchitectureIntel => InstallerCpuArchitecture.X86,
            ProcessorArchitectureArm => InstallerCpuArchitecture.Arm,
            ProcessorArchitectureAmd64 => InstallerCpuArchitecture.X64,
            ProcessorArchitectureArm64 => InstallerCpuArchitecture.Arm64,
            _ => InstallerCpuArchitecture.Unknown,
        };

    private static InstallerCpuArchitecture MapProcessArchitecture(Architecture architecture) =>
        architecture switch
        {
            Architecture.X86 => InstallerCpuArchitecture.X86,
            Architecture.X64 => InstallerCpuArchitecture.X64,
            Architecture.Arm => InstallerCpuArchitecture.Arm,
            Architecture.Arm64 => InstallerCpuArchitecture.Arm64,
            _ => InstallerCpuArchitecture.Unknown,
        };

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RtlGetVersion(ref RtlOsVersionInfoEx versionInformation);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void GetNativeSystemInfo(out NativeSystemInfo systemInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfoEx
    {
        internal uint Size;
        internal uint MajorVersion;
        internal uint MinorVersion;
        internal uint BuildNumber;
        internal uint PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string ServicePack;

        internal ushort ServicePackMajor;
        internal ushort ServicePackMinor;
        internal ushort SuiteMask;
        internal byte ProductType;
        internal byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSystemInfo
    {
        internal ProcessorInfoUnion ProcessorInfo;
        internal uint PageSize;
        internal nint MinimumApplicationAddress;
        internal nint MaximumApplicationAddress;
        internal nuint ActiveProcessorMask;
        internal uint NumberOfProcessors;
        internal uint ProcessorType;
        internal uint AllocationGranularity;
        internal ushort ProcessorLevel;
        internal ushort ProcessorRevision;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ProcessorInfoUnion
    {
        [FieldOffset(0)]
        internal uint OemId;

        [FieldOffset(0)]
        internal ushort ProcessorArchitecture;

        [FieldOffset(2)]
        internal ushort Reserved;
    }
}

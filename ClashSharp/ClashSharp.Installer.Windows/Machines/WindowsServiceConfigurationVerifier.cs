using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal enum WindowsServiceRuntimeState : uint
{
    Stopped = 0x0000_0001,
    StartPending = 0x0000_0002,
    StopPending = 0x0000_0003,
    Running = 0x0000_0004,
    ContinuePending = 0x0000_0005,
    PausePending = 0x0000_0006,
    Paused = 0x0000_0007,
}

internal sealed record WindowsServiceSnapshot(
    WindowsServiceConfiguration Configuration,
    WindowsServiceRuntimeState RuntimeState,
    string DaclSddl)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Configuration);
        Configuration.Validate();
        if (!Enum.IsDefined(RuntimeState) || string.IsNullOrWhiteSpace(DaclSddl))
        {
            throw new InstallerProtocolException(
                "installer.machine.service_snapshot_invalid");
        }

        try
        {
            _ = new RawSecurityDescriptor(DaclSddl);
        }
        catch (ArgumentException exception)
        {
            throw new InstallerProtocolException(
                "installer.machine.service_snapshot_invalid",
                exception);
        }
    }
}

internal interface IWindowsServiceConfigurationNative
{
    WindowsServiceSnapshot? Inspect(string serviceName);
}

/// <summary>
/// Independently proves the complete fixed SCM tuple without changing service state.
/// </summary>
internal sealed class WindowsServiceConfigurationVerifier
{
    private readonly IWindowsServiceConfigurationNative _native;

    internal WindowsServiceConfigurationVerifier()
        : this(WindowsServiceConfigurationNative.Instance)
    {
    }

    internal WindowsServiceConfigurationVerifier(
        IWindowsServiceConfigurationNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    internal WindowsServiceSnapshot Inspect(CancellationToken cancellationToken)
    {
        return InspectOptional(cancellationToken)
            ?? throw new InstallerProtocolException(
                "installer.machine.service_missing");
    }

    internal WindowsServiceSnapshot? InspectOptional(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            WindowsServiceSnapshot? snapshot = _native.Inspect(
                WindowsMachineDeploymentPlan.ServiceName);
            snapshot?.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
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
                "installer.machine.service_inspection_failed",
                exception);
        }
    }

    internal void VerifyPrepared(CancellationToken cancellationToken)
    {
        WindowsServiceSnapshot? actual = InspectOptional(cancellationToken);
        if (actual is null)
        {
            return;
        }

        string fence = BuildMutationFenceDaclSddl();
        if (actual.Configuration.ProcessType != WindowsServiceProcessType.OwnProcess
            || !string.Equals(
                actual.Configuration.AccountName,
                "LocalSystem",
                StringComparison.Ordinal)
            || actual.Configuration.StartMode != WindowsServiceStartMode.Disabled
            || actual.RuntimeState != WindowsServiceRuntimeState.Stopped
            || !string.Equals(actual.DaclSddl, fence, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.service_prepare_verification_failed");
        }
    }

    internal void VerifyInstalled(
        WindowsMachineDeploymentPlan plan,
        bool requireRunning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        WindowsServiceSnapshot actual = Inspect(cancellationToken);
        string expectedDacl = BuildExpectedDaclSddl(plan.Request.TargetSid);
        if (!ConfigurationMatches(actual.Configuration, plan.Service)
            || !string.Equals(actual.DaclSddl, expectedDacl, StringComparison.Ordinal)
            || requireRunning && actual.RuntimeState != WindowsServiceRuntimeState.Running)
        {
            throw new InstallerProtocolException(
                "installer.machine.service_postcondition_failed");
        }
    }

    internal void VerifyAbsent(CancellationToken cancellationToken)
    {
        if (InspectOptional(cancellationToken) is not null)
        {
            throw new InstallerProtocolException(
                "installer.machine.service_removal_verification_failed");
        }
    }

    internal static string BuildExpectedDaclSddl(string targetSid)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        string sddl = string.Concat(
            "D:",
            "(A;;CCLCSWRPWPDTLOCRRC;;;SY)",
            "(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)",
            "(A;;CCLCSWLOCRRC;;;",
            targetSid,
            ")");
        return NormalizeDacl(sddl);
    }

    internal static string BuildMutationFenceDaclSddl() => NormalizeDacl(
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)"
        + "(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)");

    internal static string NormalizeDacl(string sddl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sddl);
        var descriptor = new RawSecurityDescriptor(sddl);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    internal static bool ConfigurationMatches(
        WindowsServiceConfiguration actual,
        WindowsServiceConfiguration expected) =>
        string.Equals(actual.ServiceName, expected.ServiceName, StringComparison.Ordinal)
        && string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal)
        && string.Equals(actual.Description, expected.Description, StringComparison.Ordinal)
        && actual.ProcessType == expected.ProcessType
        && actual.StartMode == expected.StartMode
        && actual.ErrorMode == expected.ErrorMode
        && actual.DelayedAutoStart == expected.DelayedAutoStart
        && string.Equals(actual.AccountName, expected.AccountName, StringComparison.Ordinal)
        && string.Equals(actual.BinaryPath, expected.BinaryPath, StringComparison.Ordinal)
        && actual.Dependencies.SequenceEqual(expected.Dependencies, StringComparer.Ordinal);

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

internal sealed class WindowsServiceConfigurationNative
    : IWindowsServiceConfigurationNative
{
    private const uint ServiceControlManagerConnect = 0x0000_0001;
    private const uint ServiceQueryConfig = 0x0000_0001;
    private const uint ServiceQueryStatus = 0x0000_0004;
    private const uint ReadControl = 0x0002_0000;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const uint ServiceConfigDescription = 1;
    private const uint ScStatusProcessInfo = 0;
    private const uint DaclSecurityInformation = 0x0000_0004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int MaximumNativeBufferBytes = 64 * 1024;

    internal static WindowsServiceConfigurationNative Instance { get; } = new();

    private WindowsServiceConfigurationNative()
    {
    }

    public WindowsServiceSnapshot? Inspect(string serviceName)
    {
        if (!string.Equals(
                serviceName,
                WindowsMachineDeploymentPlan.ServiceName,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.service_name_invalid");
        }

        using SafeWindowsServiceHandle manager = OpenSCManager(
            machineName: null,
            databaseName: null,
            ServiceControlManagerConnect);
        ThrowIfInvalid(manager);
        using SafeWindowsServiceHandle service = OpenService(
            manager,
            serviceName,
            ServiceQueryConfig | ServiceQueryStatus | ReadControl);
        if (service.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            service.Dispose();
            if (error == ErrorServiceDoesNotExist)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        WindowsServiceConfiguration configuration = ReadConfiguration(service, serviceName);
        WindowsServiceRuntimeState state = ReadRuntimeState(service);
        string dacl = ReadDacl(service);
        return new WindowsServiceSnapshot(configuration, state, dacl);
    }

    private static WindowsServiceConfiguration ReadConfiguration(
        SafeWindowsServiceHandle service,
        string serviceName)
    {
        _ = QueryServiceConfig(service, 0, bufferBytes: 0, out uint requiredBytes);
        int firstError = Marshal.GetLastPInvokeError();
        if (firstError != ErrorInsufficientBuffer
            || requiredBytes == 0
            || requiredBytes > MaximumNativeBufferBytes)
        {
            throw new Win32Exception(firstError);
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!QueryServiceConfig(service, buffer, requiredBytes, out uint returnedBytes))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (returnedBytes > requiredBytes)
            {
                throw new InvalidDataException(
                    "The SCM returned an invalid configuration length.");
            }

            QueryServiceConfiguration native =
                Marshal.PtrToStructure<QueryServiceConfiguration>(buffer);
            string binaryPath = ReadBoundedString(buffer, requiredBytes, native.BinaryPathName);
            string displayName = ReadBoundedString(buffer, requiredBytes, native.DisplayName);
            string description = ReadDescription(service);
            string accountName = ReadBoundedString(
                buffer,
                requiredBytes,
                native.ServiceStartName);
            IReadOnlyList<string> dependencies = ReadBoundedMultiString(
                buffer,
                requiredBytes,
                native.Dependencies);
            bool delayed = ReadDelayedAutoStart(service);
            return new WindowsServiceConfiguration(
                serviceName,
                displayName,
                description,
                (WindowsServiceProcessType)native.ServiceType,
                (WindowsServiceStartMode)native.StartType,
                (WindowsServiceErrorMode)native.ErrorControl,
                delayed,
                accountName,
                binaryPath,
                dependencies);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ReadDelayedAutoStart(SafeWindowsServiceHandle service)
    {
        int size = Marshal.SizeOf<ServiceDelayedAutoStartInfo>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceConfig2(
                    service,
                    ServiceConfigDelayedAutoStartInfo,
                    buffer,
                    checked((uint)size),
                    out uint requiredBytes)
                || requiredBytes > size)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return Marshal.PtrToStructure<ServiceDelayedAutoStartInfo>(buffer)
                .DelayedAutoStart;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadDescription(SafeWindowsServiceHandle service)
    {
        _ = QueryServiceConfig2(
            service,
            ServiceConfigDescription,
            buffer: 0,
            bufferBytes: 0,
            out uint requiredBytes);
        int firstError = Marshal.GetLastPInvokeError();
        if (firstError != ErrorInsufficientBuffer
            || requiredBytes == 0
            || requiredBytes > MaximumNativeBufferBytes)
        {
            throw new Win32Exception(firstError);
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!QueryServiceConfig2(
                    service,
                    ServiceConfigDescription,
                    buffer,
                    requiredBytes,
                    out uint returnedBytes)
                || returnedBytes > requiredBytes)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            ServiceDescription native = Marshal.PtrToStructure<ServiceDescription>(buffer);
            return ReadBoundedString(buffer, requiredBytes, native.Description);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static WindowsServiceRuntimeState ReadRuntimeState(
        SafeWindowsServiceHandle service)
    {
        int size = Marshal.SizeOf<ServiceStatusProcess>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(
                    service,
                    ScStatusProcessInfo,
                    buffer,
                    checked((uint)size),
                    out uint requiredBytes)
                || requiredBytes > size)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return (WindowsServiceRuntimeState)Marshal
                .PtrToStructure<ServiceStatusProcess>(buffer).CurrentState;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadDacl(SafeWindowsServiceHandle service)
    {
        _ = QueryServiceObjectSecurity(
            service,
            DaclSecurityInformation,
            securityDescriptor: null,
            bufferBytes: 0,
            out uint requiredBytes);
        int firstError = Marshal.GetLastPInvokeError();
        if (firstError != ErrorInsufficientBuffer
            || requiredBytes == 0
            || requiredBytes > MaximumNativeBufferBytes)
        {
            throw new Win32Exception(firstError);
        }

        byte[] descriptorBytes = GC.AllocateUninitializedArray<byte>(
            checked((int)requiredBytes));
        if (!QueryServiceObjectSecurity(
                service,
                DaclSecurityInformation,
                descriptorBytes,
                requiredBytes,
                out uint returnedBytes)
            || returnedBytes > requiredBytes)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var descriptor = new RawSecurityDescriptor(descriptorBytes, 0);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    private static string ReadBoundedString(
        nint buffer,
        uint bufferBytes,
        nint value)
    {
        if (value == 0)
        {
            return string.Empty;
        }

        int offset = BoundedOffset(buffer, bufferBytes, value);
        int maximumCharacters = checked(((int)bufferBytes - offset) / sizeof(char));
        int length = 0;
        while (length < maximumCharacters
            && Marshal.ReadInt16(value, checked(length * sizeof(char))) != 0)
        {
            length++;
        }

        if (length == maximumCharacters)
        {
            throw new InvalidDataException("The SCM returned an unterminated string.");
        }

        return Marshal.PtrToStringUni(value, length) ?? string.Empty;
    }

    private static IReadOnlyList<string> ReadBoundedMultiString(
        nint buffer,
        uint bufferBytes,
        nint value)
    {
        if (value == 0)
        {
            return [];
        }

        int offset = BoundedOffset(buffer, bufferBytes, value);
        int maximumCharacters = checked(((int)bufferBytes - offset) / sizeof(char));
        var values = new List<string>();
        int position = 0;
        while (position < maximumCharacters)
        {
            if (Marshal.ReadInt16(value, checked(position * sizeof(char))) == 0)
            {
                return values.ToArray();
            }

            int start = position;
            while (position < maximumCharacters
                && Marshal.ReadInt16(value, checked(position * sizeof(char))) != 0)
            {
                position++;
            }

            if (position == maximumCharacters)
            {
                break;
            }

            values.Add(Marshal.PtrToStringUni(
                    nint.Add(value, checked(start * sizeof(char))),
                    position - start)
                ?? throw new InvalidDataException(
                    "The SCM returned an invalid dependency string."));
            position++;
        }

        throw new InvalidDataException(
            "The SCM returned an unterminated dependency set.");
    }

    private static int BoundedOffset(nint buffer, uint bufferBytes, nint value)
    {
        long offset = value.ToInt64() - buffer.ToInt64();
        if (offset < 0 || offset >= bufferBytes || (offset & 1) != 0)
        {
            throw new InvalidDataException(
                "The SCM returned a string outside its configuration buffer.");
        }

        return checked((int)offset);
    }

    private static void ThrowIfInvalid(SafeWindowsServiceHandle handle)
    {
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfiguration
    {
        internal uint ServiceType;
        internal uint StartType;
        internal uint ErrorControl;
        internal nint BinaryPathName;
        internal nint LoadOrderGroup;
        internal uint TagId;
        internal nint Dependencies;
        internal nint ServiceStartName;
        internal nint DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStartInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool DelayedAutoStart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        internal nint Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWindowsServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWindowsServiceHandle OpenService(
        SafeWindowsServiceHandle serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeWindowsServiceHandle service,
        nint serviceConfiguration,
        uint bufferBytes,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(
        SafeWindowsServiceHandle service,
        uint infoLevel,
        nint buffer,
        uint bufferBytes,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeWindowsServiceHandle service,
        uint infoLevel,
        nint buffer,
        uint bufferBytes,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceObjectSecurity(
        SafeWindowsServiceHandle service,
        uint securityInformation,
        byte[]? securityDescriptor,
        uint bufferBytes,
        out uint bytesNeeded);
}

internal sealed class SafeWindowsServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeWindowsServiceHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => CloseServiceHandle(handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint serviceHandle);
}

using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsServiceMutationNative : IWindowsServiceConfigurationNative
{
    void StopDisableAndFence(string serviceName, string fenceDaclSddl);

    void EnsureConfigured(
        WindowsServiceConfiguration configuration,
        string expectedDaclSddl);

    void Start(string serviceName);

    void StopAndDelete(string serviceName);
}

internal interface IWindowsServiceMutationDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed record WindowsServiceMutationLimits(
    int MaximumPolls,
    TimeSpan PollInterval)
{
    internal static WindowsServiceMutationLimits Default { get; } = new(
        MaximumPolls: 300,
        PollInterval: TimeSpan.FromMilliseconds(100));

    internal void Validate()
    {
        if (MaximumPolls is < 1 or > 3_600
            || PollInterval <= TimeSpan.Zero
            || PollInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPolls));
        }
    }
}

/// <summary>
/// Applies only the fixed ClashSharp service mutations and reconciles every potentially
/// acknowledged-late SCM call against an independently queried terminal postcondition.
/// </summary>
internal sealed class WindowsServiceMutation
{
    private readonly IWindowsServiceMutationNative _native;
    private readonly IWindowsServiceMutationDelay _delay;
    private readonly WindowsServiceConfigurationVerifier _verifier;
    private readonly WindowsServiceMutationLimits _limits;

    internal WindowsServiceMutation()
        : this(
            WindowsServiceMutationNative.Instance,
            WindowsServiceMutationDelay.Instance,
            WindowsServiceMutationLimits.Default)
    {
    }

    internal WindowsServiceMutation(
        IWindowsServiceMutationNative native,
        IWindowsServiceMutationDelay delay,
        WindowsServiceMutationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        _native = native;
        _delay = delay;
        _limits = limits;
        _verifier = new WindowsServiceConfigurationVerifier(native);
    }

    internal async Task StopDisableAndFenceAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        WindowsServiceSnapshot? before = InspectBeforeMutation(cancellationToken);
        if (before is not null)
        {
            RequireSafeExistingService(before);
            RequireOwnedExistingService(plan, before);
        }

        string fence = WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl();
        Exception? mutationFailure = TryMutation(() =>
            _native.StopDisableAndFence(
                WindowsMachineDeploymentPlan.ServiceName,
                fence));
        await ReconcileAsync(
                snapshot => snapshot is null
                    || snapshot.RuntimeState == WindowsServiceRuntimeState.Stopped
                    && snapshot.Configuration.StartMode == WindowsServiceStartMode.Disabled
                    && string.Equals(snapshot.DaclSddl, fence, StringComparison.Ordinal),
                mutationFailure,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task ConfigureStartAndVerifyAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        WindowsServiceSnapshot? before = InspectBeforeMutation(cancellationToken);
        string expectedDacl = WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(
            plan.Request.TargetSid);
        bool alreadyConfigured = before is not null
            && WindowsServiceConfigurationVerifier.ConfigurationMatches(
                before.Configuration,
                plan.Service)
            && string.Equals(before.DaclSddl, expectedDacl, StringComparison.Ordinal);
        if (before is not null)
        {
            RequireSafeExistingService(before);
            if (!alreadyConfigured)
            {
                RequirePreparedExistingService(before);
            }
        }

        if (!alreadyConfigured)
        {
            Exception? configurationFailure = TryMutation(() =>
                _native.EnsureConfigured(plan.Service, expectedDacl));
            await ReconcileAsync(
                    snapshot => snapshot is not null
                        && WindowsServiceConfigurationVerifier.ConfigurationMatches(
                            snapshot.Configuration,
                            plan.Service)
                        && string.Equals(
                            snapshot.DaclSddl,
                            expectedDacl,
                            StringComparison.Ordinal),
                    configurationFailure,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Exception? startFailure = TryMutation(() =>
            _native.Start(WindowsMachineDeploymentPlan.ServiceName));
        await ReconcileAsync(
                snapshot => snapshot is not null
                    && snapshot.RuntimeState == WindowsServiceRuntimeState.Running
                    && WindowsServiceConfigurationVerifier.ConfigurationMatches(
                        snapshot.Configuration,
                        plan.Service)
                    && string.Equals(
                        snapshot.DaclSddl,
                        expectedDacl,
                        StringComparison.Ordinal),
                startFailure,
                cancellationToken)
            .ConfigureAwait(false);
        _verifier.VerifyInstalled(plan, requireRunning: true, cancellationToken);
    }

    internal async Task StopDeleteAndVerifyAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        WindowsServiceSnapshot? before = InspectBeforeMutation(cancellationToken);
        if (before is null)
        {
            return;
        }

        RequireSafeExistingService(before);
        RequireOwnedExistingService(plan, before);
        Exception? mutationFailure = TryMutation(() =>
            _native.StopAndDelete(WindowsMachineDeploymentPlan.ServiceName));
        await ReconcileAsync(
                static snapshot => snapshot is null,
                mutationFailure,
                cancellationToken)
            .ConfigureAwait(false);
        _verifier.VerifyAbsent(cancellationToken);
    }

    private WindowsServiceSnapshot? InspectBeforeMutation(
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

    private async Task ReconcileAsync(
        Func<WindowsServiceSnapshot?, bool> postcondition,
        Exception? mutationFailure,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < _limits.MaximumPolls; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                WindowsServiceSnapshot? snapshot = _native.Inspect(
                    WindowsMachineDeploymentPlan.ServiceName);
                snapshot?.Validate();
                if (postcondition(snapshot))
                {
                    return;
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                mutationFailure ??= exception;
            }

            if (attempt + 1 < _limits.MaximumPolls)
            {
                await _delay.DelayAsync(_limits.PollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _ = mutationFailure;
        throw new InstallerStateUncertainException(
            "installer.machine.service_state_uncertain");
    }

    private static Exception? TryMutation(Action mutation)
    {
        try
        {
            mutation();
            return null;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return exception;
        }
    }

    private static void RequireSafeExistingService(WindowsServiceSnapshot snapshot)
    {
        snapshot.Validate();
        if (snapshot.Configuration.ProcessType != WindowsServiceProcessType.OwnProcess
            || !string.Equals(
                snapshot.Configuration.AccountName,
                "LocalSystem",
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.existing_service_unsafe");
        }
    }

    private static void RequirePreparedExistingService(WindowsServiceSnapshot snapshot)
    {
        string fence = WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl();
        if (snapshot.RuntimeState != WindowsServiceRuntimeState.Stopped
            || snapshot.Configuration.StartMode != WindowsServiceStartMode.Disabled
            || !string.Equals(snapshot.DaclSddl, fence, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.existing_service_not_prepared");
        }
    }

    private static void RequireOwnedExistingService(
        WindowsMachineDeploymentPlan plan,
        WindowsServiceSnapshot snapshot)
    {
        WindowsServiceConfiguration normalized = snapshot.Configuration with
        {
            StartMode = plan.Service.StartMode,
        };
        string expectedDacl = WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(
            plan.Request.TargetSid);
        string fence = WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl();
        if (!WindowsServiceConfigurationVerifier.ConfigurationMatches(
                normalized,
                plan.Service)
            || snapshot.Configuration.StartMode is not (
                WindowsServiceStartMode.Automatic or WindowsServiceStartMode.Disabled)
            || !(string.Equals(snapshot.DaclSddl, expectedDacl, StringComparison.Ordinal)
                || string.Equals(snapshot.DaclSddl, fence, StringComparison.Ordinal)))
        {
            throw new InstallerProtocolException(
                "installer.machine.existing_service_not_owned");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

internal sealed class WindowsServiceMutationDelay : IWindowsServiceMutationDelay
{
    internal static WindowsServiceMutationDelay Instance { get; } = new();

    private WindowsServiceMutationDelay()
    {
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal sealed class WindowsServiceMutationNative : IWindowsServiceMutationNative
{
    private const uint ServiceControlManagerConnect = 0x0000_0001;
    private const uint ServiceControlManagerCreateService = 0x0000_0002;
    private const uint ServiceQueryConfig = 0x0000_0001;
    private const uint ServiceChangeConfig = 0x0000_0002;
    private const uint ServiceQueryStatus = 0x0000_0004;
    private const uint ServiceStart = 0x0000_0010;
    private const uint ServiceStop = 0x0000_0020;
    private const uint Delete = 0x0001_0000;
    private const uint ReadControl = 0x0002_0000;
    private const uint WriteDac = 0x0004_0000;
    private const uint RequiredServiceAccess = ServiceQueryConfig
        | ServiceChangeConfig
        | ServiceQueryStatus
        | ServiceStart
        | ServiceStop
        | Delete
        | ReadControl
        | WriteDac;
    private const uint ServiceNoChange = 0xffff_ffff;
    private const uint ServiceControlStop = 1;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const uint DaclSecurityInformation = 0x0000_0004;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceMarkedForDelete = 1072;

    internal static WindowsServiceMutationNative Instance { get; } = new();

    private WindowsServiceMutationNative()
    {
    }

    public WindowsServiceSnapshot? Inspect(string serviceName) =>
        WindowsServiceConfigurationNative.Instance.Inspect(serviceName);

    public void StopDisableAndFence(string serviceName, string fenceDaclSddl)
    {
        ValidateServiceName(serviceName);
        using SafeWindowsServiceHandle manager = OpenManager(create: false);
        using SafeWindowsServiceHandle? service = OpenExisting(manager, serviceName);
        if (service is null)
        {
            return;
        }

        if (!ChangeServiceConfig(
                service,
                ServiceNoChange,
                (uint)WindowsServiceStartMode.Disabled,
                ServiceNoChange,
                binaryPathName: null,
                loadOrderGroup: null,
                tagId: 0,
                dependencies: null,
                serviceStartName: null,
                password: null,
                displayName: null))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        Exception? stopFailure = RequestStop(service);
        SetDacl(service, fenceDaclSddl);
        if (stopFailure is not null)
        {
            ExceptionDispatchInfo.Capture(stopFailure).Throw();
        }
    }

    public void EnsureConfigured(
        WindowsServiceConfiguration configuration,
        string expectedDaclSddl)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.ValidateExpected();
        using SafeWindowsServiceHandle manager = OpenManager(create: true);
        SafeWindowsServiceHandle? existing = OpenExisting(
            manager,
            configuration.ServiceName);
        using SafeWindowsServiceHandle service = existing is null
            ? Create(manager, configuration)
            : existing;
        if (existing is not null
            && !ChangeServiceConfig(
                service,
                (uint)configuration.ProcessType,
                (uint)configuration.StartMode,
                (uint)configuration.ErrorMode,
                configuration.BinaryPath,
                loadOrderGroup: string.Empty,
                tagId: 0,
                dependencies: "\0",
                serviceStartName: configuration.AccountName,
                password: null,
                configuration.DisplayName))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var delayed = new ServiceDelayedAutoStartInfo
        {
            DelayedAutoStart = configuration.DelayedAutoStart,
        };
        if (!ChangeServiceConfig2(
                service,
                ServiceConfigDelayedAutoStartInfo,
                ref delayed))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        SetDescription(service, configuration.Description);
        SetDacl(service, expectedDaclSddl);
    }

    public void Start(string serviceName)
    {
        ValidateServiceName(serviceName);
        using SafeWindowsServiceHandle manager = OpenManager(create: false);
        using SafeWindowsServiceHandle service = OpenRequired(manager, serviceName);
        if (!StartService(service, argumentCount: 0, arguments: 0))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceAlreadyRunning)
            {
                throw new Win32Exception(error);
            }
        }
    }

    public void StopAndDelete(string serviceName)
    {
        ValidateServiceName(serviceName);
        using SafeWindowsServiceHandle manager = OpenManager(create: false);
        using SafeWindowsServiceHandle? service = OpenExisting(manager, serviceName);
        if (service is null)
        {
            return;
        }

        if (!ChangeServiceConfig(
                service,
                ServiceNoChange,
                (uint)WindowsServiceStartMode.Disabled,
                ServiceNoChange,
                binaryPathName: null,
                loadOrderGroup: null,
                tagId: 0,
                dependencies: null,
                serviceStartName: null,
                password: null,
                displayName: null))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        Exception? stopFailure = RequestStop(service);
        if (!DeleteService(service))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceMarkedForDelete)
            {
                throw new Win32Exception(error);
            }
        }

        if (stopFailure is not null)
        {
            ExceptionDispatchInfo.Capture(stopFailure).Throw();
        }
    }

    private static SafeWindowsServiceHandle OpenManager(bool create)
    {
        uint access = ServiceControlManagerConnect;
        if (create)
        {
            access |= ServiceControlManagerCreateService;
        }

        SafeWindowsServiceHandle manager = OpenSCManager(
            machineName: null,
            databaseName: null,
            access);
        ThrowIfInvalid(manager);
        return manager;
    }

    private static SafeWindowsServiceHandle? OpenExisting(
        SafeWindowsServiceHandle manager,
        string serviceName)
    {
        SafeWindowsServiceHandle service = OpenService(
            manager,
            serviceName,
            RequiredServiceAccess);
        if (!service.IsInvalid)
        {
            return service;
        }

        int error = Marshal.GetLastPInvokeError();
        service.Dispose();
        if (error == ErrorServiceDoesNotExist)
        {
            return null;
        }

        throw new Win32Exception(error);
    }

    private static SafeWindowsServiceHandle OpenRequired(
        SafeWindowsServiceHandle manager,
        string serviceName) =>
        OpenExisting(manager, serviceName)
        ?? throw new Win32Exception(ErrorServiceDoesNotExist);

    private static SafeWindowsServiceHandle Create(
        SafeWindowsServiceHandle manager,
        WindowsServiceConfiguration configuration)
    {
        SafeWindowsServiceHandle service = CreateService(
            manager,
            configuration.ServiceName,
            configuration.DisplayName,
            RequiredServiceAccess,
            (uint)configuration.ProcessType,
            (uint)configuration.StartMode,
            (uint)configuration.ErrorMode,
            configuration.BinaryPath,
            loadOrderGroup: null,
            tagId: 0,
            dependencies: null,
            serviceStartName: null,
            password: null);
        ThrowIfInvalid(service);
        return service;
    }

    private static Exception? RequestStop(SafeWindowsServiceHandle service)
    {
        if (ControlService(service, ServiceControlStop, out _))
        {
            return null;
        }

        int error = Marshal.GetLastPInvokeError();
        return error == ErrorServiceNotActive ? null : new Win32Exception(error);
    }

    private static void SetDescription(
        SafeWindowsServiceHandle service,
        string description)
    {
        nint text = Marshal.StringToHGlobalUni(description);
        try
        {
            var info = new ServiceDescription
            {
                Description = text,
            };
            if (!ChangeServiceConfig2(service, ServiceConfigDescription, ref info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static void SetDacl(
        SafeWindowsServiceHandle service,
        string daclSddl)
    {
        string normalized = WindowsServiceConfigurationVerifier.NormalizeDacl(daclSddl);
        var descriptor = new RawSecurityDescriptor(normalized);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(descriptor.BinaryLength);
        descriptor.GetBinaryForm(bytes, 0);
        if (!SetServiceObjectSecurity(service, DaclSecurityInformation, bytes))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void ValidateServiceName(string serviceName)
    {
        if (!string.Equals(
                serviceName,
                WindowsMachineDeploymentPlan.ServiceName,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.service_name_invalid");
        }
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
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
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

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWindowsServiceHandle CreateService(
        SafeWindowsServiceHandle serviceControlManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        SafeWindowsServiceHandle service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        SafeWindowsServiceHandle service,
        uint infoLevel,
        ref ServiceDelayedAutoStartInfo info);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        SafeWindowsServiceHandle service,
        uint infoLevel,
        ref ServiceDescription info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(
        SafeWindowsServiceHandle service,
        uint argumentCount,
        nint arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        SafeWindowsServiceHandle service,
        uint control,
        out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(SafeWindowsServiceHandle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceObjectSecurity(
        SafeWindowsServiceHandle service,
        uint securityInformation,
        byte[] securityDescriptor);
}

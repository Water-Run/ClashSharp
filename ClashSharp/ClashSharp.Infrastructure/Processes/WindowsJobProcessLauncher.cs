using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Infrastructure.Processes;

internal enum WindowsJobProcessLaunchStage
{
    BeforeAssignment,
    BeforeResume,
}

internal sealed class WindowsJobProcessCleanupException(
    bool assignedToJob,
    Exception launchFailure,
    Exception cleanupFailure)
    : AggregateException(
        "The suspended mihomo child failed to launch and its termination could not be confirmed.",
        launchFailure,
        cleanupFailure)
{
    public bool AssignedToJob { get; } = assignedToJob;
}

internal sealed record WindowsJobProcessStartInfo(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    bool CaptureOutput,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

internal sealed class WindowsJobProcess : IDisposable
{
    public WindowsJobProcess(Process process, StreamReader? standardOutput, StreamReader? standardError)
    {
        Process = process;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public Process Process { get; }

    public StreamReader? StandardOutput { get; }

    public StreamReader? StandardError { get; }

    public void Dispose()
    {
        StandardOutput?.Dispose();
        StandardError?.Dispose();
        Process.Dispose();
    }
}

internal interface IWindowsProcessJob : IDisposable
{
    void AssignProcess(SafeFileHandle processHandle);

    void TerminateAndWaitForEmpty(TimeSpan timeout);

    Task TerminateAndWaitForEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class WindowsKillOnCloseJob : IWindowsProcessJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly SafeFileHandle _handle;

    private WindowsKillOnCloseJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static WindowsKillOnCloseJob Create()
    {
        EnsureWindows();
        SafeFileHandle handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Unable to create the mihomo process Job Object.");
        }

        JobObjectExtendedLimitInformation limits = new()
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!NativeMethods.SetInformationJobObject(
                handle,
                JobObjectInformationClass.ExtendedLimitInformation,
                ref limits,
                checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Unable to configure the mihomo process Job Object.");
        }

        return new WindowsKillOnCloseJob(handle);
    }

    public void AssignProcess(SafeFileHandle processHandle)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to assign the suspended mihomo process to its Job Object.");
        }
    }

    /// <summary>Terminates every process in this job and confirms the job becomes empty.</summary>
    public void TerminateAndWaitForEmpty(TimeSpan timeout)
    {
        ValidateTimeout(timeout);
        BeginTermination();

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (GetActiveProcessCount() != 0)
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw CreateHandoffTimeoutException();
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>Terminates every process in this job and asynchronously confirms the job becomes empty.</summary>
    public async Task TerminateAndWaitForEmptyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateTimeout(timeout);
        BeginTermination();

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (GetActiveProcessCount() != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed >= timeout)
            {
                throw CreateHandoffTimeoutException();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
    }

    private void BeginTermination()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (!NativeMethods.TerminateJobObject(_handle, 1))
        {
            int terminateError = Marshal.GetLastWin32Error();
            if (GetActiveProcessCount() != 0)
            {
                throw new Win32Exception(
                    terminateError,
                    "Unable to terminate the mihomo process Job Object.");
            }
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    private static TimeoutException CreateHandoffTimeoutException()
    {
        return new TimeoutException(
            "The mihomo process Job Object did not become empty before the handoff timeout.");
    }

    private uint GetActiveProcessCount()
    {
        if (!NativeMethods.QueryInformationJobObject(
                _handle,
                JobObjectInformationClass.BasicAccountingInformation,
                out JobObjectBasicAccountingInformation accounting,
                checked((uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>()),
                IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to query the mihomo process Job Object.");
        }

        return accounting.ActiveProcesses;
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Job Objects require Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    private enum JobObjectInformationClass
    {
        BasicAccountingInformation = 1,
        ExtendedLimitInformation = 9,
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // This interop is isolated and SafeFileHandle owns every returned handle.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            SafeFileHandle job,
            JobObjectInformationClass informationClass,
            ref JobObjectExtendedLimitInformation jobObjectInformation,
            uint jobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(
            SafeFileHandle job,
            JobObjectInformationClass informationClass,
            out JobObjectBasicAccountingInformation jobObjectInformation,
            uint jobObjectInformationLength,
            IntPtr returnLength);
#pragma warning restore SYSLIB1054
    }
}

internal sealed class WindowsJobProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint ResumeThreadFailed = uint.MaxValue;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitFailed = uint.MaxValue;
    private const uint CleanupWaitMilliseconds = 5000;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;

    private readonly Action<WindowsJobProcessLaunchStage, int>? _faultInjector;

    public WindowsJobProcessLauncher(Action<WindowsJobProcessLaunchStage, int>? faultInjector = null)
    {
        _faultInjector = faultInjector;
    }

    public WindowsJobProcess Start(IWindowsProcessJob job, WindowsJobProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.WorkingDirectory);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Suspended Windows process creation requires Windows.");
        }

        SafeFileHandle? processHandle = null;
        SafeFileHandle? threadHandle = null;
        SafeFileHandle? outputReadHandle = null;
        SafeFileHandle? outputWriteHandle = null;
        SafeFileHandle? errorReadHandle = null;
        SafeFileHandle? errorWriteHandle = null;
        SafeFileHandle? inputHandle = null;
        ProcThreadAttributeList? attributeList = null;
        Process? process = null;
        StreamReader? standardOutput = null;
        StreamReader? standardError = null;
        IntPtr environmentBlock = IntPtr.Zero;
        bool processCreated = false;
        bool assignedToJob = false;

        try
        {
            StartupInfoEx startupInfo = new();
            startupInfo.StartupInfo.Size = Marshal.SizeOf<StartupInfo>();
            uint creationFlags = CreateSuspended | CreateNoWindow;
            bool inheritHandles = false;

            if (startInfo.CaptureOutput)
            {
                CreateOutputPipe(out outputReadHandle, out outputWriteHandle);
                CreateOutputPipe(out errorReadHandle, out errorWriteHandle);
                inputHandle = CreateInheritedNullInput();
                attributeList = ProcThreadAttributeList.Create([inputHandle, outputWriteHandle, errorWriteHandle]);
                startupInfo.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
                startupInfo.AttributeList = attributeList.Pointer;
                startupInfo.StartupInfo.Flags = StartfUseStdHandles;
                startupInfo.StartupInfo.StandardInput = inputHandle.DangerousGetHandle();
                startupInfo.StartupInfo.StandardOutput = outputWriteHandle.DangerousGetHandle();
                startupInfo.StartupInfo.StandardError = errorWriteHandle.DangerousGetHandle();
                creationFlags |= ExtendedStartupInfoPresent;
                inheritHandles = true;
            }

            if (startInfo.EnvironmentVariables is not null)
            {
                environmentBlock = CreateEnvironmentBlock(startInfo.EnvironmentVariables);
                creationFlags |= CreateUnicodeEnvironment;
            }

            StringBuilder commandLine = BuildCommandLine(startInfo.FileName, startInfo.Arguments);
            if (!NativeMethods.CreateProcess(
                    startInfo.FileName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles,
                    creationFlags,
                    environmentBlock,
                    startInfo.WorkingDirectory,
                    ref startupInfo,
                    out ProcessInformation processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the suspended mihomo process.");
            }

            processCreated = true;
            processHandle = new SafeFileHandle(processInformation.Process, ownsHandle: true);
            threadHandle = new SafeFileHandle(processInformation.Thread, ownsHandle: true);
            outputWriteHandle?.Dispose();
            outputWriteHandle = null;
            errorWriteHandle?.Dispose();
            errorWriteHandle = null;
            inputHandle?.Dispose();
            inputHandle = null;

            process = Process.GetProcessById(processInformation.ProcessId);
            // Force Process to retain its own wait/query handle while the suspended child
            // still exists. A lazy open after a very fast exit cannot recover ExitCode.
            _ = process.Handle;
            if (startInfo.CaptureOutput)
            {
                standardOutput = CreateReader(outputReadHandle!);
                outputReadHandle = null;
                standardError = CreateReader(errorReadHandle!);
                errorReadHandle = null;
            }

            _faultInjector?.Invoke(WindowsJobProcessLaunchStage.BeforeAssignment, processInformation.ProcessId);
            job.AssignProcess(processHandle);
            assignedToJob = true;
            _faultInjector?.Invoke(WindowsJobProcessLaunchStage.BeforeResume, processInformation.ProcessId);
            uint previousSuspendCount = NativeMethods.ResumeThread(threadHandle);
            if (previousSuspendCount == ResumeThreadFailed)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resume the Job-owned mihomo process.");
            }

            WindowsJobProcess result = new(process, standardOutput, standardError);
            process = null;
            standardOutput = null;
            standardError = null;
            return result;
        }
        catch (Exception launchFailure)
        {
            Exception? cleanupFailure = processCreated && processHandle is not null
                ? TryTerminateCreatedProcess(processHandle)
                : null;
            standardOutput?.Dispose();
            standardError?.Dispose();
            process?.Dispose();
            if (cleanupFailure is not null)
            {
                throw new WindowsJobProcessCleanupException(
                    assignedToJob,
                    launchFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(launchFailure).Throw();
            throw;
        }
        finally
        {
            threadHandle?.Dispose();
            processHandle?.Dispose();
            attributeList?.Dispose();
            inputHandle?.Dispose();
            outputWriteHandle?.Dispose();
            errorWriteHandle?.Dispose();
            outputReadHandle?.Dispose();
            errorReadHandle?.Dispose();
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    private static IntPtr CreateEnvironmentBlock(
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        SortedDictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in environmentVariables)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(value);
            if (name.Contains('=', StringComparison.Ordinal)
                || name.Contains('\0', StringComparison.Ordinal)
                || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("Process environment names and values must not contain separators.");
            }

            environment[name] = value;
        }

        StringBuilder block = new();
        foreach ((string name, string value) in environment)
        {
            block.Append(name);
            block.Append('=');
            block.Append(value);
            block.Append('\0');
        }

        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static void CreateOutputPipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle)
    {
        SecurityAttributes attributes = new()
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        if (!NativeMethods.CreatePipe(out readHandle, out writeHandle, ref attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create a mihomo output pipe.");
        }

        if (!NativeMethods.SetHandleInformation(readHandle, HandleFlagInherit, 0))
        {
            int error = Marshal.GetLastWin32Error();
            readHandle.Dispose();
            writeHandle.Dispose();
            throw new Win32Exception(error, "Unable to make the parent mihomo pipe handle non-inheritable.");
        }
    }

    private static SafeFileHandle CreateInheritedNullInput()
    {
        SecurityAttributes attributes = new()
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        SafeFileHandle handle = NativeMethods.CreateFile(
            "NUL",
            GenericRead,
            FileShareRead | FileShareWrite,
            ref attributes,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Unable to open the null input handle for mihomo.");
        }

        return handle;
    }

    private static StreamReader CreateReader(SafeFileHandle readHandle)
    {
        // CreatePipe returns synchronous handles. StreamReader's async APIs still perform
        // non-blocking caller-facing reads through the runtime's synchronous-handle path.
        FileStream stream = new(readHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        try
        {
            return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static Exception? TryTerminateCreatedProcess(SafeFileHandle processHandle)
    {
        int terminateError = 0;
        if (!NativeMethods.TerminateProcess(processHandle, 1))
        {
            terminateError = Marshal.GetLastWin32Error();
        }

        uint waitResult = NativeMethods.WaitForSingleObject(processHandle, CleanupWaitMilliseconds);
        if (waitResult == WaitObject0)
        {
            // The safety invariant is confirmed termination. TerminateProcess may race a
            // process that exited between the failed launch operation and cleanup.
            return null;
        }

        if (waitResult == WaitFailed)
        {
            return new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Waiting for the failed suspended mihomo child termination failed.");
        }

        return terminateError == 0
            ? new TimeoutException("The failed suspended mihomo child did not terminate within five seconds.")
            : new Win32Exception(terminateError, "Unable to terminate the failed suspended mihomo child.");
    }

    private static StringBuilder BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        StringBuilder builder = new();
        AppendQuotedArgument(builder, fileName);
        foreach (string argument in arguments)
        {
            builder.Append(' ');
            AppendQuotedArgument(builder, argument);
        }

        return builder;
    }

    private static void AppendQuotedArgument(StringBuilder builder, string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        int backslashCount = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', checked((backslashCount * 2) + 1));
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(character);
        }

        builder.Append('\\', checked(backslashCount * 2));
        builder.Append('"');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    private sealed class ProcThreadAttributeList : IDisposable
    {
        private IntPtr _handleList;

        private ProcThreadAttributeList(IntPtr pointer, IntPtr handleList)
        {
            Pointer = pointer;
            _handleList = handleList;
        }

        public IntPtr Pointer { get; private set; }

        public static ProcThreadAttributeList Create(IReadOnlyList<SafeFileHandle> handles)
        {
            nuint size = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            int sizingError = Marshal.GetLastWin32Error();
            if (size == 0)
            {
                throw new Win32Exception(sizingError, "Unable to size the mihomo inherited-handle list.");
            }

            IntPtr pointer = Marshal.AllocHGlobal(checked((nint)size));
            IntPtr handleList = IntPtr.Zero;
            bool initialized = false;
            try
            {
                if (!NativeMethods.InitializeProcThreadAttributeList(pointer, 1, 0, ref size))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to initialize the mihomo inherited-handle list.");
                }

                initialized = true;
                handleList = Marshal.AllocHGlobal(checked(handles.Count * IntPtr.Size));
                for (int index = 0; index < handles.Count; index++)
                {
                    Marshal.WriteIntPtr(handleList, checked(index * IntPtr.Size), handles[index].DangerousGetHandle());
                }

                if (!NativeMethods.UpdateProcThreadAttribute(
                        pointer,
                        0,
                        ProcThreadAttributeHandleList,
                        handleList,
                        checked((nuint)(handles.Count * IntPtr.Size)),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to restrict mihomo child handle inheritance.");
                }

                return new ProcThreadAttributeList(pointer, handleList);
            }
            catch
            {
                if (initialized)
                {
                    NativeMethods.DeleteProcThreadAttributeList(pointer);
                }

                Marshal.FreeHGlobal(pointer);
                if (handleList != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(handleList);
                }

                throw;
            }
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }

            if (_handleList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_handleList);
                _handleList = IntPtr.Zero;
            }
        }
    }

    private static class NativeMethods
    {
#pragma warning disable CA1838 // CreateProcessW requires a mutable command-line buffer.
#pragma warning disable SYSLIB1054 // This interop requires mutable CreateProcessW command lines and native startup structs.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreatePipe(
            out SafeFileHandle readPipe,
            out SafeFileHandle writePipe,
            ref SecurityAttributes pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref SecurityAttributes securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint ResumeThread(SafeFileHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            nuint attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
#pragma warning restore SYSLIB1054
#pragma warning restore CA1838
    }
}

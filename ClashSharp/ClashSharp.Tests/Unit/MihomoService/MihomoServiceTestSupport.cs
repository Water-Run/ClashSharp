using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.MihomoService;

internal static class MihomoServiceTestSupport
{
    internal const string Token =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static readonly SecurityIdentifier TestUserSid =
        new("S-1-5-21-100-200-300-1001");

    internal static MihomoServiceOptions CreateOptions(
        string rootPath,
        SecurityIdentifier? allowedSid = null,
        string? token = null)
    {
        SecurityIdentifier sid = allowedSid ?? TestUserSid;
        string authenticationToken = token ?? Token;
        return new MihomoServiceOptions(
            Path.Combine(rootPath, "mihomo.exe"),
            Path.Combine(rootPath, "runtime.yaml"),
            MihomoServiceIpcProtocol.BuildPipeName(sid.Value, authenticationToken),
            authenticationToken,
            sid,
            Path.Combine(rootPath, "staged"));
    }

    internal static string ComputeHash(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static string BuildManagedServiceConfiguration(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        List<string> managed = [];
        if (!HasRootKey(content, "external-controller"))
        {
            managed.Add("external-controller: 127.0.0.1:9090");
        }

        if (!HasRootKey(content, "secret"))
        {
            managed.Add($"secret: '{Token}'");
        }

        if (!HasRootKey(content, "allow-lan"))
        {
            managed.Add("allow-lan: false");
        }

        if (!HasRootKey(content, "bind-address"))
        {
            managed.Add("bind-address: 127.0.0.1");
        }

        if (!HasRootKey(content, "mode"))
        {
            managed.Add("mode: rule");
        }

        if (!HasRootKey(content, "tun"))
        {
            managed.AddRange(
            [
                "tun:",
                "  enable: true",
                "  stack: system",
                "  auto-route: true",
                "  auto-detect-interface: true",
                "  strict-route: false",
                "  dns-hijack:",
                "    - any:53",
            ]);
        }

        string prefix = managed.Count == 0 ? string.Empty : string.Join('\n', managed) + "\n";
        return prefix + content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    private static bool HasRootKey(string content, string key)
    {
        return content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Any(line => line.StartsWith(key + ":", StringComparison.Ordinal));
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}

internal sealed class MihomoServiceTemporaryDirectory : IDisposable
{
    internal MihomoServiceTemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "clashsharp-mihomo-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(
                Path,
                "*",
                SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class FakeMihomoChildProcess : IMihomoChildProcess
{
    private const int NotExited = int.MinValue;

    private readonly bool _blockStop;
    private readonly TextReader? _standardError;
    private readonly TextReader? _standardOutput;
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _exitObservationRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _stopRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    private int _exitCode = NotExited;
    private int _stopCalls;

    internal FakeMihomoChildProcess(
        string name,
        int id,
        ConcurrentQueue<string>? events = null,
        bool blockStop = false,
        string? standardOutput = null,
        string? standardError = null,
        TextReader? standardOutputReader = null,
        TextReader? standardErrorReader = null)
    {
        Name = name;
        Id = id;
        Events = events;
        _blockStop = blockStop;
        _standardOutput = standardOutputReader
            ?? (standardOutput is null ? null : new StringReader(standardOutput));
        _standardError = standardErrorReader
            ?? (standardError is null ? null : new StringReader(standardError));
    }

    internal string Name { get; }

    internal ConcurrentQueue<string>? Events { get; }

    internal Exception? StopFailure { get; set; }

    internal Exception? OutputReadFailure { get; set; }

    internal bool BlockExitObservation { get; set; }

    internal TaskCompletionSource<object?> ExitObservationEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<object?> StopEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool StopCompleted { get; private set; }

    internal int StopCalls => Volatile.Read(ref _stopCalls);

    public int Id { get; }

    public bool HasExited => Volatile.Read(ref _exitCode) != NotExited;

    public int? ExitCode
    {
        get
        {
            int exitCode = Volatile.Read(ref _exitCode);
            return exitCode == NotExited ? null : exitCode;
        }
    }

    public TextReader? StandardOutput => OutputReadFailure is null
        ? _standardOutput
        : throw OutputReadFailure;

    public TextReader? StandardError => OutputReadFailure is null
        ? _standardError
        : throw OutputReadFailure;

    internal void Exit(int exitCode)
    {
        if (Interlocked.CompareExchange(ref _exitCode, exitCode, NotExited) == NotExited)
        {
            Events?.Enqueue($"exit:{Name}");
            _exit.TrySetResult(exitCode);
        }
    }

    internal void ReleaseStop()
    {
        _stopRelease.TrySetResult(null);
    }

    internal void ReleaseExitObservation()
    {
        _exitObservationRelease.TrySetResult(null);
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        ExitObservationEntered.TrySetResult(null);
        if (BlockExitObservation)
        {
            await _exitObservationRelease.Task.ConfigureAwait(false);
        }

        _ = await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopTreeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Interlocked.Increment(ref _stopCalls);
        Events?.Enqueue($"stop-enter:{Name}");
        StopEntered.TrySetResult(null);
        if (_blockStop)
        {
            await _stopRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (StopFailure is not null)
        {
            throw StopFailure;
        }

        Exit(0);
        StopCompleted = true;
        Events?.Enqueue($"stop-complete:{Name}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Events?.Enqueue($"dispose:{Name}");
        Exit(1);
        _standardOutput?.Dispose();
        _standardError?.Dispose();
    }
}

internal sealed class GatedTextReader : TextReader
{
    private readonly Exception? _completionFailure;
    private readonly TaskCompletionSource<object?> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GatedTextReader(Exception? completionFailure = null)
    {
        _completionFailure = completionFailure;
    }

    internal TaskCompletionSource<object?> ReadEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Release()
    {
        _release.TrySetResult(null);
    }

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        ReadEntered.TrySetResult(null);
        await _release.Task.ConfigureAwait(false);
        if (_completionFailure is not null)
        {
            throw _completionFailure;
        }

        return null;
    }
}

internal sealed class FakeMihomoChildProcessLauncher : IMihomoChildProcessLauncher
{
    private readonly object _syncLock = new();
    private readonly Queue<FakeMihomoChildProcess> _processes;
    private readonly List<MihomoChildStartRequest> _requests = [];

    internal FakeMihomoChildProcessLauncher(
        IEnumerable<FakeMihomoChildProcess> processes,
        ConcurrentQueue<string>? events = null)
    {
        _processes = new Queue<FakeMihomoChildProcess>(processes);
        Events = events;
    }

    internal ConcurrentQueue<string>? Events { get; }

    internal IReadOnlyList<MihomoChildStartRequest> Requests
    {
        get
        {
            lock (_syncLock)
            {
                return _requests.ToArray();
            }
        }
    }

    public IMihomoChildProcess Start(MihomoChildStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_syncLock)
        {
            if (_processes.Count == 0)
            {
                throw new InvalidOperationException("No fake mihomo process remains.");
            }

            FakeMihomoChildProcess process = _processes.Dequeue();
            _requests.Add(request);
            Events?.Enqueue($"start:{process.Name}");
            return process;
        }
    }
}

internal sealed record FakeReadinessProbeCall(
    MihomoControllerAuthority Authority,
    IMihomoChildProcess Process,
    MihomoRuntimeConfigurationPlan Expected,
    TimeSpan Timeout);

internal sealed class FakeMihomoControllerReadinessProbe : IMihomoControllerReadinessProbe
{
    private readonly ConcurrentQueue<FakeReadinessProbeCall> _calls = new();

    internal Func<
        MihomoControllerAuthority,
        IMihomoChildProcess,
        MihomoRuntimeConfigurationPlan,
        TimeSpan,
        CancellationToken,
        Task<MihomoServiceIpcEffectiveConfiguration>>?
        Handler
    { get; set; }

    internal IReadOnlyList<FakeReadinessProbeCall> Calls => _calls.ToArray();

    public Task<MihomoServiceIpcEffectiveConfiguration> WaitUntilReadyAsync(
        MihomoControllerAuthority authority,
        IMihomoChildProcess process,
        MihomoRuntimeConfigurationPlan expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _calls.Enqueue(new FakeReadinessProbeCall(authority, process, expected, timeout));
        return Handler?.Invoke(authority, process, expected, timeout, cancellationToken)
            ?? Task.FromResult(new MihomoServiceIpcEffectiveConfiguration
            {
                ControllerReady = true,
                MixedPort = expected.MixedPort,
                Mode = expected.Mode,
                TunEnabled = expected.TunEnabled,
            });
    }
}

internal sealed class MihomoChildSupervisorTestContext : IAsyncDisposable
{
    private readonly MihomoServiceTemporaryDirectory _temporaryDirectory = new();

    internal MihomoChildSupervisorTestContext(
        IEnumerable<FakeMihomoChildProcess> processes,
        IReadOnlyList<TimeSpan>? restartBackoffs = null,
        ConcurrentQueue<string>? events = null,
        FakeMihomoControllerReadinessProbe? readinessProbe = null)
    {
        Options = MihomoServiceTestSupport.CreateOptions(_temporaryDirectory.Path);
        File.WriteAllText(Options.MihomoPath, "test executable placeholder");
        WriteConfiguration("mixed-port: 7890\n");
        Logs = new MihomoServiceLogBuffer(Options);
        RuntimeLogs = new MihomoRuntimeLogBuffer(Logs);
        Launcher = new FakeMihomoChildProcessLauncher(processes, events);
        ReadinessProbe = readinessProbe ?? new FakeMihomoControllerReadinessProbe();
        Supervisor = new MihomoChildSupervisor(
            Options,
            new MihomoGenerationStore(Options, protectDirectory: false),
            new MihomoEffectiveConfigurationMaterializer(protectDirectory: false),
            Launcher,
            ReadinessProbe,
            Logs,
            RuntimeLogs,
            startupObservationDelay: TimeSpan.Zero,
            restartBackoffs: restartBackoffs ?? [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            stopTimeout: TimeSpan.FromSeconds(1),
            readinessTimeout: TimeSpan.FromSeconds(1),
            serviceVersion: "1.2.3-test");
        ControllerBroker = new MihomoServiceControllerBroker(
            Supervisor,
            new MihomoNamedPipeControllerTransportFactory(),
            RuntimeLogs,
            Logs);
    }

    internal MihomoServiceOptions Options { get; }

    internal MihomoServiceLogBuffer Logs { get; }

    internal MihomoRuntimeLogBuffer RuntimeLogs { get; }

    internal FakeMihomoChildProcessLauncher Launcher { get; }

    internal FakeMihomoControllerReadinessProbe ReadinessProbe { get; }

    internal MihomoChildSupervisor Supervisor { get; }

    internal MihomoServiceControllerBroker ControllerBroker { get; }

    internal string WriteConfiguration(string content)
    {
        LastConfigurationText = MihomoServiceTestSupport.BuildManagedServiceConfiguration(content);
        File.WriteAllText(Options.ConfigPath, LastConfigurationText);
        return MihomoServiceTestSupport.ComputeHash(LastConfigurationText);
    }

    internal string LastConfigurationText { get; private set; } = string.Empty;

    public async ValueTask DisposeAsync()
    {
        await Supervisor.DisposeAsync().ConfigureAwait(false);
        _temporaryDirectory.Dispose();
    }
}

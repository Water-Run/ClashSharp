using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;
using ClashSharp.Tests.Unit.MihomoService;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>Launcher whose behavior is scripted per attempt and which records any overlap of live children.</summary>
internal sealed class ScriptedMihomoLauncher : IMihomoChildProcessLauncher
{
    private readonly object _gate = new();
    private int _nextProcessId = 5000;

    internal List<FakeMihomoChildProcess> Started { get; } = [];

    internal int Attempts { get; private set; }

    /// <summary>Gets whether any launch happened while another child was still alive.</summary>
    internal bool OverlapDetected { get; private set; }

    /// <summary>Makes the next launch attempt throw; consumed by that attempt.</summary>
    internal bool FailNextLaunch { get; set; }

    /// <summary>Makes every launch attempt throw until cleared.</summary>
    internal bool FailAllLaunches { get; set; }

    /// <summary>Makes the next launched child already exited when returned.</summary>
    internal bool ExitNextLaunchImmediately { get; set; }

    internal FakeMihomoChildProcess[] Snapshot()
    {
        lock (_gate)
        {
            return [.. Started];
        }
    }

    public IMihomoChildProcess Start(MihomoChildStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            Attempts++;
            if (Started.Any(process => !process.HasExited))
            {
                OverlapDetected = true;
            }

            if (FailNextLaunch || FailAllLaunches)
            {
                FailNextLaunch = false;
                throw new InvalidOperationException("scripted launch failure");
            }

            FakeMihomoChildProcess process = new($"child-{Attempts}", ++_nextProcessId);
            if (ExitNextLaunchImmediately)
            {
                ExitNextLaunchImmediately = false;
                process.Exit(9);
            }

            Started.Add(process);
            return process;
        }
    }
}

/// <summary>Real supervisor, store and materializer over a temp directory with scripted process and readiness fakes.</summary>
internal sealed class SupervisorHarness : IAsyncDisposable
{
    private readonly MihomoServiceTemporaryDirectory _directory = new();
    private int _configurationSerial;

    internal SupervisorHarness(
        IReadOnlyList<TimeSpan>? restartBackoffs = null,
        bool generationLayout = false)
    {
        Options = MihomoServiceTestSupport.CreateOptions(_directory.Path, generationLayout: generationLayout);
        File.WriteAllText(Options.MihomoPath, "test executable placeholder");
        Logs = new MihomoServiceLogBuffer(Options);
        RuntimeLogs = new MihomoRuntimeLogBuffer(Logs);
        Launcher = new ScriptedMihomoLauncher();
        Probe = new FakeMihomoControllerReadinessProbe();
        Supervisor = new MihomoChildSupervisor(
            Options,
            new MihomoGenerationStore(Options, protectDirectory: false),
            new MihomoEffectiveConfigurationMaterializer(protectDirectory: false),
            Launcher,
            Probe,
            Logs,
            RuntimeLogs,
            startupObservationDelay: TimeSpan.Zero,
            restartBackoffs: restartBackoffs ?? [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            stopTimeout: TimeSpan.FromSeconds(1),
            readinessTimeout: TimeSpan.FromSeconds(1),
            serviceVersion: "harness");
        Broker = new MihomoServiceControllerBroker(
            Supervisor,
            new MihomoNamedPipeControllerTransportFactory(),
            RuntimeLogs,
            Logs);
        Hash = WriteConfiguration(0);
    }

    internal MihomoServiceOptions Options { get; }

    internal MihomoServiceLogBuffer Logs { get; }

    internal MihomoRuntimeLogBuffer RuntimeLogs { get; }

    internal ScriptedMihomoLauncher Launcher { get; }

    internal FakeMihomoControllerReadinessProbe Probe { get; }

    internal MihomoChildSupervisor Supervisor { get; }

    internal MihomoServiceControllerBroker Broker { get; }

    /// <summary>Gets the hash of the configuration currently written at the installed path.</summary>
    internal string Hash { get; private set; }

    /// <summary>Gets the effective-configuration files currently owned by the supervisor.</summary>
    internal string[] EffectiveFiles()
    {
        string directory = Path.Combine(Options.RuntimeDirectory, "effective");
        return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
    }

    internal string[] StagedGenerationFiles()
    {
        return Directory.Exists(Options.ServiceDataDirectory)
            ? Directory.GetFiles(Options.ServiceDataDirectory, "generation-*.yaml")
            : [];
    }

    /// <summary>Writes a new distinct configuration and returns its hash.</summary>
    internal string WriteNewConfiguration()
    {
        Hash = WriteConfiguration(++_configurationSerial);
        return Hash;
    }

    internal void MakeReadinessFail()
    {
        Probe.Handler = (_, _, _, _, _) => Task.FromException<MihomoServiceIpcEffectiveConfiguration>(
            new MihomoControllerNotReadyException("scripted readiness failure"));
    }

    internal void MakeReadinessSucceed()
    {
        Probe.Handler = null;
    }

    internal MihomoServiceIpcControllerBinding BindingOf(MihomoServiceIpcSnapshot snapshot)
    {
        return new MihomoServiceIpcControllerBinding
        {
            ServiceSessionId = snapshot.SessionId,
            Generation = snapshot.ActiveGeneration!.Value,
            ConfigurationHash = snapshot.ActiveConfigurationHash!,
            DataGenerationId = snapshot.ActiveDataGenerationId,
        };
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(2), timeout.Token);
        }
    }

    private string WriteConfiguration(int serial)
    {
        string text = MihomoServiceTestSupport.BuildManagedServiceConfiguration($"mixed-port: {7000 + serial}\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Options.ConfigPath)!);
        File.WriteAllText(Options.ConfigPath, text);
        return MihomoServiceTestSupport.ComputeHash(text);
    }

    public async ValueTask DisposeAsync()
    {
        await Supervisor.DisposeAsync();
        _directory.Dispose();
    }
}

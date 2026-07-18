using System.Globalization;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Startup;

return await StartupProbeProgram.RunAsync(args);

internal static class StartupProbeProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        IReadOnlyDictionary<string, string> options = ParseOptions(args);
        string instanceKey = GetRequiredOption(options, "--instance-key");
        string tracePath = GetRequiredOption(options, "--trace");
        string readyPath = GetRequiredOption(options, "--ready");
        string releasePath = GetRequiredOption(options, "--release");
        using Semaphore semaphore = new(1, 1, $"Local\\ClashSharp.StartupProbe.{instanceKey}");
        bool ownsSemaphore = semaphore.WaitOne(0);

        try
        {
            ProcessLifetimeRunner lifetime = new();
            ProbePrimaryInstanceBootstrap primaryInstance = new(ownsSemaphore, tracePath);
            ApplicationBootstrapper bootstrapper = new(
                primaryInstance,
                () =>
                {
                    AppendTrace(tracePath, "host-build");
                    return new ProbeApplicationHost(tracePath, readyPath, releasePath);
                },
                lifetime);

            ApplicationLaunchResult result = await bootstrapper.LaunchAsync(
                new AppLaunchRequest(string.Empty),
                CancellationToken.None);
            if (result.Disposition == ApplicationLaunchDisposition.Running)
            {
                await lifetime.StopAsync(CancellationToken.None);
            }

            return result.Disposition is ApplicationLaunchDisposition.Running or ApplicationLaunchDisposition.Redirected
                ? 0
                : 2;
        }
        finally
        {
            if (ownsSemaphore)
            {
                semaphore.Release();
            }
        }
    }

    internal static void AppendTrace(string path, string eventName)
    {
        string line = $"{eventName}:{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}";
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                File.AppendAllText(path, line);
                return;
            }
            catch (IOException) when (attempt < 49)
            {
                Thread.Sleep(10);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        if (args.Count % 2 != 0)
        {
            throw new ArgumentException("Probe options must be supplied as name/value pairs.", nameof(args));
        }

        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index += 2)
        {
            options.Add(args[index], args[index + 1]);
        }

        return options;
    }

    private static string GetRequiredOption(IReadOnlyDictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Required probe option '{name}' was not supplied.", nameof(options));
        }

        return value;
    }

    private sealed class ProbePrimaryInstanceBootstrap(bool ownsSemaphore, string tracePath) : IPrimaryInstanceBootstrap
    {
        public Task<PrimaryInstanceOwnership> AcquireAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ownsSemaphore)
            {
                AppendTrace(tracePath, "secondary-redirected");
                return Task.FromResult(PrimaryInstanceOwnership.Redirected);
            }

            return Task.FromResult(PrimaryInstanceOwnership.Primary);
        }
    }

    private sealed class ProbeApplicationHost(
        string tracePath,
        string readyPath,
        string releasePath) : IApplicationHost
    {
        public async Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            AppendTrace(tracePath, "host-start");
            ProbeNetworkRuntime network = new(tracePath);
            network.ApplyRuleTakeover();
            await File.WriteAllTextAsync(readyPath, "ready", cancellationToken);
            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(20));
            while (!File.Exists(releasePath))
            {
                await Task.Delay(25, timeoutSource.Token);
            }

            return StartupStepResult.Succeeded();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            AppendTrace(tracePath, "host-stop");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            AppendTrace(tracePath, "host-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeNetworkRuntime(string tracePath)
    {
        public void ApplyRuleTakeover()
        {
            AppendTrace(tracePath, "network-mode-rule-takeover");
            AppendTrace(tracePath, "core-start");
            AppendTrace(tracePath, "system-proxy-enable");
        }
    }
}

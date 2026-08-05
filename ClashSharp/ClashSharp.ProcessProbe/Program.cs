using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

return await ProcessProbeProgram.RunAsync(args);

internal static class ProcessProbeProgram
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return 64;
        }

        return args[0] switch
        {
            "emit" => await EmitAsync(args),
            "arguments" => await WriteArgumentsAsync(args),
            "environment" => await WriteEnvironmentAsync(args),
            "-d" => await RunCoreProbeAsync(args),
            "-v" => await SpawnChildAsync(),
            "spawn-child" => await SpawnChildAsync(),
            "child-hang" => await HangAsync("child-ready"),
            "hang" => await HangAsync("root-ready"),
            _ => 64,
        };
    }

    private static async Task<int> EmitAsync(IReadOnlyList<string> args)
    {
        int count = int.Parse(args[1], CultureInfo.InvariantCulture);
        int exitCode = int.Parse(args[2], CultureInfo.InvariantCulture);
        Task standardOutput = WriteLinesAsync(Console.Out, "out", count);
        Task standardError = WriteLinesAsync(Console.Error, "err", count);
        await Task.WhenAll(standardOutput, standardError);
        return exitCode;
    }

    private static async Task<int> SpawnChildAsync()
    {
        using Process child = StartChild();
        Console.WriteLine($"child:{child.Id.ToString(CultureInfo.InvariantCulture)}");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        GC.KeepAlive(child);
        return 0;
    }

    private static async Task<int> WriteArgumentsAsync(IReadOnlyList<string> args)
    {
        for (int index = 1; index < args.Count; index++)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(args[index]));
            await Console.Out.WriteLineAsync("arg:" + encoded);
        }

        await Console.Out.FlushAsync();
        return 0;
    }

    private static async Task<int> WriteEnvironmentAsync(IReadOnlyList<string> args)
    {
        for (int index = 1; index < args.Count; index++)
        {
            string value = Environment.GetEnvironmentVariable(args[index]) ?? "<missing>";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            await Console.Out.WriteLineAsync("env:" + encoded);
        }

        await Console.Out.FlushAsync();
        return 0;
    }

    private static async Task<int> EmitCoreStartupFailureAsync()
    {
        Task standardOutput = WriteCoreFailureStreamAsync(Console.Out, "core-out");
        Task standardError = WriteCoreFailureStreamAsync(Console.Error, "core-err");
        await Task.WhenAll(standardOutput, standardError);
        return 23;
    }

    private static async Task<int> RunCoreProbeAsync(IReadOnlyList<string> args)
    {
        int configArgumentIndex = -1;
        for (int index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "-f", StringComparison.Ordinal))
            {
                configArgumentIndex = index;
                break;
            }
        }

        if (configArgumentIndex >= 0 && configArgumentIndex + 1 < args.Count)
        {
            string configuration = await File.ReadAllTextAsync(args[configArgumentIndex + 1]);
            if (configuration.Contains("process-probe: delayed-exit", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                return 42;
            }

            if (configuration.Contains("process-probe: spawn-child", StringComparison.Ordinal))
            {
                using Process child = StartChild();
                string childPath = Path.Combine(
                    Path.GetDirectoryName(args[configArgumentIndex + 1])!,
                    "child.pid");
                await File.WriteAllTextAsync(
                    childPath,
                    child.Id.ToString(CultureInfo.InvariantCulture));
                if (configuration.Contains("startup-failure", StringComparison.Ordinal))
                {
                    return 23;
                }

                if (configuration.Contains("then-exit", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300));
                    return 42;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan);
                GC.KeepAlive(child);
                return 0;
            }
        }

        return await EmitCoreStartupFailureAsync();
    }

    private static Process StartChild()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("child-hang");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The process probe child could not start.");
    }

    private static async Task WriteCoreFailureStreamAsync(TextWriter writer, string prefix)
    {
        for (int index = 0; index < 20; index++)
        {
            await writer.WriteLineAsync($"{prefix}:{index.ToString("D2", CultureInfo.InvariantCulture)}");
        }

        await writer.WriteLineAsync(prefix + "-final");
        await writer.FlushAsync();
    }

    private static async Task<int> HangAsync(string marker)
    {
        Console.WriteLine($"{marker}:{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task WriteLinesAsync(TextWriter writer, string prefix, int count)
    {
        for (int index = 0; index < count; index++)
        {
            await writer.WriteLineAsync($"{prefix}:{index.ToString("D5", CultureInfo.InvariantCulture)}");
        }

        await writer.FlushAsync();
    }
}

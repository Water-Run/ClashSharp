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
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("child-hang");
        Process child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The process probe child could not start.");
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

/*
 * Mihomo Service Options
 * Parses service host command-line options
 *
 * @author: WaterRun
 * @file: ClashSharp.MihomoService/MihomoServiceOptions.cs
 * @date: 2026-06-24
 */

namespace ClashSharp.MihomoService;

/// <summary>Command-line options for the mihomo service host.</summary>
internal sealed record MihomoServiceOptions(string MihomoPath, string ConfigPath, string WorkDirectory)
{
    /// <summary>Parses service command-line arguments.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Parsed options.</returns>
    /// <exception cref="ArgumentException">A required argument is missing.</exception>
    public static MihomoServiceOptions Parse(string[] args)
    {
        string mihomoPath = ReadOption(args, "--mihomo");
        string configPath = ReadOption(args, "--config");
        string workDirectory = ReadOption(args, "--workdir");
        return new MihomoServiceOptions(mihomoPath, configPath, workDirectory);
    }

    /// <summary>Reads a required option value.</summary>
    private static string ReadOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                string value = args[index + 1];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        throw new ArgumentException($"Missing required option {name}.", nameof(args));
    }
}

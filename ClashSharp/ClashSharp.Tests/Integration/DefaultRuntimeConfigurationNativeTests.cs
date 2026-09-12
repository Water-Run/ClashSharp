extern alias ClashSharpUi;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClashSharp.ApplicationModel.Processes;
using ClashSharp.Infrastructure.Processes;
using ClashSharp.Model;
using Xunit.Abstractions;
using RuntimeConfigurationBuilder = ClashSharpUi::ClashSharp.Service.MihomoRuntimeConfigurationBuilder;

namespace ClashSharp.Tests.Integration;

/// <summary>Validates actual production defaults with the pinned bundled core in test-only mode.</summary>
public sealed class DefaultRuntimeConfigurationNativeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ClashSharpMode.Disabled, false)]
    [InlineData(ClashSharpMode.Standby, false)]
    [InlineData(ClashSharpMode.RuleTakeover, false)]
    [InlineData(ClashSharpMode.FullTakeover, false)]
    [InlineData(ClashSharpMode.RuleTakeover, true)]
    [InlineData(ClashSharpMode.FullTakeover, true)]
    public async Task ProductionDefault_BundledCoreAcceptsConfiguration(ClashSharpMode mode, bool effectiveTunEnabled)
    {
        string assemblyDirectory = Path.GetDirectoryName(typeof(RuntimeConfigurationBuilder).Assembly.Location)!;
        string binaryPath = Path.Combine(assemblyDirectory, "Binaries", "mihomo.exe");
        string manifestPath = Path.Combine(assemblyDirectory, "Binaries", "mihomo-manifest.json");
        Assert.True(File.Exists(binaryPath), $"Required bundled native validator is missing: {binaryPath}");
        Assert.True(File.Exists(manifestPath), $"Required bundled core manifest is missing: {manifestPath}");
        string binaryHash;
        using (FileStream binary = File.OpenRead(binaryPath))
        {
            binaryHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(binary));
        }
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(manifest.RootElement.GetProperty("sha256").GetString(), binaryHash);
        output.WriteLine($"Binary: {binaryPath}; SHA256: {binaryHash}; Mode: {mode}; EffectiveTun: {effectiveTunEnabled}");

        // Only this unique directory and fixed test credential are passed to mihomo.
        // The -t command validates TUN/DNS syntax without starting listeners or routing.
        string directory = Path.Combine(Path.GetTempPath(), "clashsharp-native-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string configuration = RuntimeConfigurationBuilder.BuildDefaultConfiguration(
                10000, mode, effectiveTunEnabled, new string('1', 64));
            string candidatePath = Path.Combine(directory, "config.yaml");
            await File.WriteAllTextAsync(candidatePath, configuration, new UTF8Encoding(false));
            ProcessRequest request = new(binaryPath, ["-t", "-d", directory, "-f", candidatePath],
                TimeSpan.FromSeconds(15), workingDirectory: directory);
            ProcessRunResult result = await new WindowsProcessRunner().RunAsync(request, CancellationToken.None);
            string diagnostic = $"Outcome={result.Outcome}; ExitCode={result.ExitCode}; Failure={result.FailureMessage}"
                + $"{Environment.NewLine}stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}";
            output.WriteLine(diagnostic);
            Assert.True(result.Outcome == ProcessRunOutcome.Completed && result.ExitCode == 0
                && string.IsNullOrEmpty(result.FailureMessage), diagnostic);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

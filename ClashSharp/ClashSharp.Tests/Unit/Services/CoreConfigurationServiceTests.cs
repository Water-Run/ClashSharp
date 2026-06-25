/*
 * Core Configuration Service Tests
 * Verifies core configuration generation and import through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/CoreConfigurationServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for local mihomo configuration management.</summary>
public sealed class CoreConfigurationServiceTests
{
    /// <summary>Verifies runtime configuration generation uses injected settings and scoped paths.</summary>
    [Fact]
    public void EnsureConfiguration_UsesInjectedSettingsAndWritesRuntimeConfiguration()
    {
        using TempDirectory tempDirectory = new();
        FakeCoreConfigurationSettings settings = new()
        {
            ActiveProfileId = ProfileCatalogIds.BuiltInDirect,
            MixedPort = 19090,
            TransparentProxyEnabled = true,
        };
        CoreConfigurationService service = CreateService(tempDirectory.Path, settings);

        CoreConfigurationState state = service.EnsureConfiguration(ClashSharpMode.FullTakeover);

        string configurationText = File.ReadAllText(state.ConfigPath);
        Assert.True(state.Exists);
        Assert.Equal(Path.Combine(tempDirectory.Path, "config.yaml"), state.ConfigPath);
        Assert.Contains("mixed-port: 19090", configurationText, StringComparison.Ordinal);
        Assert.Contains("mode: global", configurationText, StringComparison.Ordinal);
        Assert.Contains("tun:\n", configurationText, StringComparison.Ordinal);
    }

    /// <summary>Verifies profile import uses injected metrics, validation, and localization dependencies.</summary>
    [Fact]
    public async Task ImportProfileConfigurationAsync_UsesInjectedMetricsValidatorAndLocalization()
    {
        using TempDirectory tempDirectory = new();
        FakeCoreConfigurationProfileMetrics metrics = new()
        {
            NodeCount = 3,
            RuleCount = 4,
        };
        FakeCoreConfigurationValidator validator = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, metrics: metrics, validator: validator);

        ProfileImportResult result = await service.ImportProfileConfigurationAsync(
            "profile:one",
            " Test Profile ",
            """
            proxies:
              - name: DIRECT
                type: direct
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - DIRECT
            rules:
              - MATCH,DIRECT
            """,
            CancellationToken.None);

        Assert.Equal("profile-one", result.ProfileId);
        Assert.Equal("Test Profile", result.ProfileName);
        Assert.Equal(3, result.NodeCount);
        Assert.Equal(4, result.RuleCount);
        Assert.Equal("imported", result.Message);
        Assert.True(File.Exists(result.ConfigPath));
        Assert.Equal([new CoreValidationRequest(Path.GetDirectoryName(result.ConfigPath)!, result.ConfigPath)], validator.Requests);
    }

    private static CoreConfigurationService CreateService(
        string configurationDirectory,
        FakeCoreConfigurationSettings? settings = null,
        FakeCoreConfigurationProfileMetrics? metrics = null,
        FakeCoreConfigurationValidator? validator = null)
    {
        return new CoreConfigurationService(
            configurationDirectory,
            settings ?? new FakeCoreConfigurationSettings(),
            metrics ?? new FakeCoreConfigurationProfileMetrics(),
            validator ?? new FakeCoreConfigurationValidator(),
            key => key switch
            {
                "CoreConfiguration.Imported" => "imported",
                "CoreConfiguration.Validated" => "validated",
                _ => key,
            });
    }

    private sealed class FakeCoreConfigurationSettings : ICoreConfigurationSettings
    {
        public bool TransparentProxyEnabled { get; init; }

        public int MixedPort { get; init; } = 7890;

        public string ActiveProfileId { get; init; } = ProfileCatalogIds.BuiltInDirect;
    }

    private sealed class FakeCoreConfigurationProfileMetrics : ICoreConfigurationProfileMetrics
    {
        public int NodeCount { get; init; }

        public int RuleCount { get; init; }

        public int CountNodes(string configurationText)
        {
            return NodeCount;
        }

        public int CountRules(string configurationText)
        {
            return RuleCount;
        }
    }

    private sealed class FakeCoreConfigurationValidator : ICoreConfigurationValidator
    {
        public List<CoreValidationRequest> Requests { get; } = [];

        public Task ValidateAsync(string workingDirectory, string configurationPath, CancellationToken cancellationToken)
        {
            Requests.Add(new CoreValidationRequest(workingDirectory, configurationPath));
            return Task.CompletedTask;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clashsharp-core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private readonly record struct CoreValidationRequest(string WorkingDirectory, string ConfigurationPath);
}

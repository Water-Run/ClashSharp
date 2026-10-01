using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class RuntimeConfigurationTransactionTests
{
    [Theory]
    [InlineData("live")]
    [InlineData("snapshot")]
    [InlineData("manifest")]
    public async Task DnsTiles_AppliedDnsIsPublishedOnlyWhileConfigurationEvidenceMatches(string changedFile)
    {
        using TempDirectory directory = new();
        CoreConfigurationService service = CreateService(directory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult applied = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover, true, 17890, new RecordingRuntime(), CancellationToken.None);
        RuntimeConfigurationIntegrityObservation observation = service.ObserveRuntimeConfigurationIntegrity();
        Assert.True(observation.IsKnown);
        Assert.Equal("fake-ip", Assert.IsType<RuntimeDnsConfiguration>(observation.Dns).Mode);
        RuntimeConfigurationIntegrityObservation repeated = service.ObserveRuntimeConfigurationIntegrity();
        Assert.Equal(observation, repeated);
        Assert.Equal(observation.GetHashCode(), repeated.GetHashCode());
        string target = changedFile switch
        {
            "snapshot" => Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "runtime-generations"), "*.yaml")),
            "manifest" => Path.Combine(directory.Path, "config.runtime-state.json"),
            _ => applied.Configuration.ConfigPath,
        };
        File.AppendAllText(target, "\n# changed outside the runtime transaction\n");
        RuntimeConfigurationIntegrityObservation modified = service.ObserveRuntimeConfigurationIntegrity();
        Assert.False(modified.IsKnown);
        Assert.Null(modified.Dns);
        Assert.NotNull(observation.Dns);
    }
}

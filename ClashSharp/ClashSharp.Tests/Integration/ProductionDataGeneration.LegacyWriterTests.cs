extern alias ClashSharpUi;
using System.Reflection;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Startup;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task SettingsCutover_EveryLegacyPropertyWriterIsRejectedWithoutChangingEitherStore()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        Dictionary<string, object> legacy = new() { ["MixedPort"] = 54321, ["NotificationEnabled"] = false };
        var beforeLegacy = legacy.ToArray();
        UiService.AppSettingsService settings = new(legacy);
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(fixture.Manager), settings).ExecuteAsync(new(""), CancellationToken.None)).Outcome);
        var before = fixture.Authority.CaptureSnapshot();
        PropertyInfo[] properties = typeof(UiService.AppSettingsService).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod is not null && property.SetMethod is not null).ToArray();
        Assert.True(properties.Length >= 31);

        foreach (PropertyInfo property in properties)
        {
            object? value = property.GetValue(settings);
            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => property.SetValue(settings, value));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
        }

        Assert.Equal(beforeLegacy, legacy.ToArray());
        var after = fixture.Authority.CaptureSnapshot();
        Assert.True(before.Generation.IsSameGeneration(after.Generation));
        Assert.Same(before.Envelope, after.Envelope);
    }
}

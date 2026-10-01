extern alias ClashSharpUi;
using System.Reflection;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Factory = ClashSharpUi::ClashSharp.Hosting.ClashSharpAppHostFactory;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;

namespace ClashSharp.Tests.Integration;

public sealed class ProductionSettingsCutoverTests
{
    [Fact]
    public async Task SettingsCutover_CompiledHostSharesOneAuthorityAndDoesNotExposePhysicalSettingsStorage()
    {
        Diagnostics diagnostics = new();
        // Build and resolve pure ownership objects only; never run product startup or native effects.
        await using AppHost host = Factory.Build(new(""), _ => throw new InvalidOperationException("Unexpected window creation."),
            new ApplicationLifetimeRequestChannel(), diagnostics, UiService.InstallerTransactionState.Clear);
        IServiceProvider services = Assert.IsAssignableFrom<IServiceProvider>(typeof(AppHost)
            .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));

        GenerationSettingsAuthority authority = services.GetRequiredService<GenerationSettingsAuthority>();
        Assert.Same(authority, services.GetRequiredService<ISettingsAuthority>());
        Assert.Same(authority, services.GetRequiredService<IRuntimeSettingsAuthority>());
        Assert.Same(authority, services.GetRequiredService<ISettingsRuntimeGroupReset>());
        Assert.Same(authority, services.GetRequiredService<ISettingsApplicationRecovery>());
        Assert.Null(services.GetService<ISettingsRepository>());
        Assert.Null(services.GetService<SettingsAuthoritySession>());
        Assert.Null(services.GetService<StartupSettingsCoordinator>());
        Assert.Null(services.GetService<ConnectionSamplingSettingsCoordinator>());

        DataGenerationManager generations = services.GetRequiredService<DataGenerationManager>();
        Assert.True(generations.IsAwaitingInitialization);
        Assert.Throws<DataGenerationManagerException>(() => authority.CaptureSnapshot());
        Assert.IsType<UiData.GenerationProfileCatalog>(services.GetRequiredService<UiService.IProfileCatalog>());
        Assert.IsType<UiData.GenerationLogStorage>(services.GetRequiredService<UiService.ILogStorage>());
        Assert.IsType<UiData.GenerationTriggerDefinitionStore>(services.GetRequiredService<ITriggerDefinitionStore>());
        Assert.True(generations.IsAwaitingInitialization);
        await host.StopAsync(CancellationToken.None);
        Assert.Empty(diagnostics.Records);
    }

    private sealed class Diagnostics : IStartupDiagnosticSink
    {
        public List<StartupDiagnosticRecord> Records { get; } = [];
        public void Record(StartupDiagnosticRecord record) => Records.Add(record);
        public void RecordFailure(StartupDiagnosticRecord record, Exception exception) => Records.Add(record);
    }
}

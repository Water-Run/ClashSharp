using System.Net;
using System.Net.Http;
using System.Reflection;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class RuntimeConfigurationTransactionTests
{
    [Fact]
    public async Task TransitionFlushKeepsOldConfigurationIdentityThroughSamplerAndSqlite()
    {
        using TempDirectory directory = new();
        CoreConfigurationService configuration = CreateService(Path.Combine(directory.Path, "mihomo"), new RecordingValidator());
        RecordingRuntime runtime = new();
        await configuration.ApplyRuntimeConfigurationAsync(ClashSharpMode.Standby, false, 17890, runtime, CancellationToken.None);
        RuntimeConfigurationIntegrityObservation installed = configuration.ObserveRuntimeConfigurationIntegrity();
        string originalLiveText = File.ReadAllText(configuration.GetState().ConfigPath);
        await using LogStorageService logs = new(Path.Combine(directory.Path, "logs.db"), () => "failed-target");
        TrafficHandler handler = new();
        using HttpClient http = new(handler);
        MihomoControllerClient controller = new(http, new Uri("http://127.0.0.1:9090"));
        MihomoConnectionService connections = (MihomoConnectionService)typeof(MihomoConnectionService)
            .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(MihomoControllerClient)], null)!.Invoke([controller]);
        ConnectionSamplingService sampler = new(new EnabledSampling(), new ConnectionSamplingSourceAdapter(connections, configuration),
            new ConnectionSamplingStorageAdapter(logs), key => key);
        await sampler.SampleOnceAsync(CancellationToken.None);
        handler.Upload = 300;
        runtime.Preparing = async (baseline, token) =>
        {
            Assert.False(configuration.ObserveRuntimeConfigurationIntegrity().IsKnown);
            Assert.Equal(installed.StatisticsIdentity, baseline.StatisticsIdentity);
            Assert.Equal(originalLiveText, await File.ReadAllTextAsync(configuration.GetState().ConfigPath, token));
            await sampler.FlushAsync(baseline, token);
        };
        await configuration.ApplyRuntimeConfigurationAsync(ClashSharpMode.Disabled, false, 17890, runtime, CancellationToken.None);
        TrafficStatisticRow row = Assert.Single(logs.GetProfileTrafficRows(10));
        Assert.Equal("builtin-direct", row.Label);
        Assert.Equal(300, row.UploadBytes);
        Assert.Equal(300, logs.GetTrafficStatisticsSummary().TotalUploadBytes);
    }

    [Fact]
    public async Task CancelledTransitionPreparationRestoresTheVerifiedManifest()
    {
        using TempDirectory directory = new();
        using CancellationTokenSource cancellation = new();
        CoreConfigurationService configuration = CreateService(directory.Path, new RecordingValidator());
        RecordingRuntime runtime = new();
        await configuration.ApplyRuntimeConfigurationAsync(ClashSharpMode.Standby, false, 17890, runtime, CancellationToken.None);
        RuntimeConfigurationIntegrityObservation installed = configuration.ObserveRuntimeConfigurationIntegrity();
        runtime.Preparing = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configuration.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Disabled, false, 17890, runtime, cancellation.Token));
        Assert.Equal(installed, configuration.ObserveRuntimeConfigurationIntegrity());
    }

    private sealed class EnabledSampling : IConnectionSamplingSettings
    {
        public bool IsEnabled => true;
        public int IntervalSeconds => 30;
    }

    private sealed class TrafficHandler : HttpMessageHandler
    {
        public long Upload { get; set; } = 100;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"connections\":[],\"uploadTotal\":{Upload},\"downloadTotal\":0}}") });
    }
}

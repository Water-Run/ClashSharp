extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using ClashSharp.Settings;
using UiComposition = ClashSharpUi::ClashSharp.Presentation.Composition;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;
using UiView = ClashSharpUi::ClashSharp.ViewModel;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Theory]
    [InlineData("appearance")]
    [InlineData("startup")]
    [InlineData("network")]
    public async Task SettingsRecovery_NativeApplicationFailureLeavesTheShellStartupUsable(string kind)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        StartupPlatform startup = new();
        AppearanceSurface appearance = new();
        NetworkSurface network = new();
        if (kind == "appearance")
        {
            fixture.LegacyValues[SettingsRegistry.Keys.AppThemeMode.Value] = (int)AppThemeMode.Dark;
            appearance.BeforeApply = _ => throw new IOException("appearance unavailable");
        }
        if (kind == "startup")
        {
            fixture.LegacyValues[SettingsRegistry.Keys.LaunchAtStartupEnabled.Value] = true;
            startup.State = UiService.StartupLaunchTaskState.Other;
        }
        if (kind == "network") { network.Unknown = true; }
        ConfigureRealRuntime(fixture, startup, appearance, network);
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());

        var result = await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings)
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Warning, result.Outcome);
        Assert.True(settings.HasFailedSettingsApplications);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        UiView.SettingsRecoveryViewModel page = new(key => key, fixture.Authority, new RecoveryPageErrors(), () => { });
        page.Refresh();
        Assert.NotEmpty(page.Issues);
        Assert.True(page.HasIssues);
    }

    [Fact]
    public async Task SettingsRecovery_RestartKeepsFailedIdentityUntilTheUserExplicitlyRetries()
    {
        await using DataGenerationTestDirectory directory = new();
        SettingsApplicationBatch original;
        await using (Fixture initial = new(directory))
        {
            ConfigureRealRuntime(initial, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface { Unknown = true });
            var result = await initial.CreateStartupStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(initial.Manager),
                new UiService.AppSettingsService(new Dictionary<string, object>())).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
            Assert.Equal(StartupStepOutcome.Warning, result.Outcome);
            original = initial.Containers[0].Session.Snapshot.PendingApplications.Single(batch => batch.State == SettingsApplicationBatchState.Failed);
        }
        await using Fixture reopened = new(directory);
        NetworkSurface network = new();
        int applications = 0;
        network.BeforeApply = (_, _) => applications++;
        ConfigureRealRuntime(reopened, new StartupPlatform(), new AppearanceSurface(), network);
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        var restarted = await reopened.CreateStartupStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(reopened.Manager), settings)
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
        Assert.Equal(StartupStepOutcome.Warning, restarted.Outcome);
        var retained = reopened.Containers[0].Session.Snapshot.PendingApplications.Single(batch => batch.State == SettingsApplicationBatchState.Failed);
        Assert.Equal(original.BatchId, retained.BatchId);
        Assert.Equal(original.AttemptId, retained.AttemptId);
        Assert.Equal(0, applications);
        UiView.SettingsRecoveryViewModel page = new(key => key, reopened.Authority, new RecoveryPageErrors(), () => { });
        page.Refresh();
        var issue = Assert.Single(page.Issues);

        await page.RetryAsync(issue, CancellationToken.None);

        Assert.True(applications > 0);
        Assert.Empty(page.Issues);
        Assert.False(page.HasError);
        Assert.False(settings.HasFailedSettingsApplications);
        Assert.Equal(SettingAppliedStateKind.Verified, reopened.Containers[0].Session.Snapshot.Applied[SettingsRegistry.Keys.CurrentMode].Kind);
    }

    [Fact]
    public async Task SettingsRecovery_ApplicationStatusNotificationDoesNotRequireAChangedDesiredValue()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface { Unknown = true });
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        _ = await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings)
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
        int notified = 0;
        using var subscription = new UiComposition.SettingsApplicationSubscription(settings, () => notified++);
        var before = fixture.Authority.CaptureSnapshot();
        var failed = before.Envelope.PendingApplications.Single(batch => batch.State == SettingsApplicationBatchState.Failed);

        var result = await fixture.Authority.RetryApplicationAsync(before, failed.BatchId, CancellationToken.None);

        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Outcome.Status);
        Assert.Equal(1, notified);
        Assert.True(settings.HasFailedSettingsApplications);
        Assert.NotEqual(failed.AttemptId, result.Outcome.Envelope!.PendingApplications.Single(batch => batch.BatchId == failed.BatchId).AttemptId);
    }

    private sealed class RecoveryPageErrors : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected recovery page error.", error.Exception);
    }
}

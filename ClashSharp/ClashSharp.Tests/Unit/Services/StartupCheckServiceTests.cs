using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class StartupCheckServiceTests
{
    [Fact]
    public async Task GetChecksAsync_CollectsEachRequiredProbeOnceOffTheCallingPath()
    {
        using ManualResetEventSlim subscriptionStarted = new();
        using ManualResetEventSlim releaseSubscription = new();
        FakeStartupCheckProbe probe = new()
        {
            SubscriptionStarted = subscriptionStarted,
            ReleaseSubscription = releaseSubscription,
        };
        RecordingErrorSink errorSink = new();
        StartupCheckService service = new(probe, GetString, errorSink);

        Task<IReadOnlyList<StartupCheckItem>> checksTask =
            service.GetChecksAsync(CancellationToken.None);

        Assert.True(subscriptionStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(checksTask.IsCompleted);
        releaseSubscription.Set();

        IReadOnlyList<StartupCheckItem> checks = await checksTask;

        Assert.Equal(4, checks.Count);
        Assert.All(checks, static check => Assert.True(check.IsHealthy));
        Assert.Equal(1, probe.HasSubscriptionCalls);
        Assert.Equal(1, probe.TransparentProxyEnabledCalls);
        Assert.Equal(1, probe.MihomoStatusCalls);
        Assert.Equal(1, probe.FallbackRegisteredCalls);
        Assert.Equal(1, probe.WindowsProxyStateCalls);
        Assert.Equal(1, probe.MixedPortCalls);
        Assert.Equal(1, probe.StaleProxyCalls);
        Assert.Empty(errorSink.Errors);
    }

    [Fact]
    public async Task GetChecksAsync_WhenProbeFails_UsesSafeTextAndReportsOriginalError()
    {
        InvalidOperationException failure = new("private filesystem detail");
        FakeStartupCheckProbe probe = new()
        {
            SubscriptionFailure = failure,
        };
        RecordingErrorSink errorSink = new();
        StartupCheckService service = new(probe, GetString, errorSink);

        IReadOnlyList<StartupCheckItem> checks =
            await service.GetChecksAsync(CancellationToken.None);

        StartupCheckItem subscription = checks[0];
        Assert.False(subscription.IsHealthy);
        Assert.Equal("Proxy subscription", subscription.Title);
        Assert.Equal("Check unavailable", subscription.Description);
        Assert.DoesNotContain(failure.Message, subscription.Description, StringComparison.Ordinal);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("startup-check-subscription", error.OperationName);
        Assert.Same(failure, error.Exception);
    }

    [Fact]
    public async Task GetChecksAsync_WhenTransparentProxyIsDisabled_SkipsServiceStatusProbe()
    {
        FakeStartupCheckProbe probe = new()
        {
            TransparentProxyEnabled = false,
        };
        StartupCheckService service = new(probe, GetString, new RecordingErrorSink());

        IReadOnlyList<StartupCheckItem> checks =
            await service.GetChecksAsync(CancellationToken.None);

        Assert.True(checks[1].IsHealthy);
        Assert.Equal("Disabled", checks[1].Description);
        Assert.Equal(1, probe.TransparentProxyEnabledCalls);
        Assert.Equal(0, probe.MihomoStatusCalls);
    }

    [Fact]
    public async Task GetChecksAsync_WhenDiagnosticSinkFails_StillReturnsSafeSnapshot()
    {
        FakeStartupCheckProbe probe = new()
        {
            SubscriptionFailure = new UnauthorizedAccessException("private registry detail"),
        };
        ThrowingErrorSink errorSink = new();
        StartupCheckService service = new(probe, GetString, errorSink);

        IReadOnlyList<StartupCheckItem> checks =
            await service.GetChecksAsync(CancellationToken.None);

        Assert.Equal("Check unavailable", checks[0].Description);
        Assert.Equal(1, errorSink.CallCount);
    }

    [Fact]
    public async Task GetChecksAsync_WhenOwnerCancels_StopsWithoutReportingCancellation()
    {
        using ManualResetEventSlim subscriptionStarted = new();
        using ManualResetEventSlim releaseSubscription = new();
        FakeStartupCheckProbe probe = new()
        {
            SubscriptionStarted = subscriptionStarted,
            ReleaseSubscription = releaseSubscription,
        };
        RecordingErrorSink errorSink = new();
        StartupCheckService service = new(probe, GetString, errorSink);
        using CancellationTokenSource cancellation = new();

        Task<IReadOnlyList<StartupCheckItem>> checksTask =
            service.GetChecksAsync(cancellation.Token);
        Assert.True(subscriptionStarted.Wait(TimeSpan.FromSeconds(5)));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checksTask);

        Assert.Equal(1, probe.HasSubscriptionCalls);
        Assert.Equal(0, probe.TransparentProxyEnabledCalls);
        Assert.Empty(errorSink.Errors);
        releaseSubscription.Set();
    }

    [Fact]
    public async Task GetChecksAsync_WhenAlreadyCancelled_DoesNotStartAnyProbe()
    {
        FakeStartupCheckProbe probe = new();
        RecordingErrorSink errorSink = new();
        StartupCheckService service = new(probe, GetString, errorSink);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetChecksAsync(cancellation.Token));

        Assert.Equal(0, probe.HasSubscriptionCalls);
        Assert.Equal(0, probe.TransparentProxyEnabledCalls);
        Assert.Empty(errorSink.Errors);
    }

    [Fact]
    public void BuildTransparentProxyCheck_WhenStatusIsUnknown_PreservesUnknownDescription()
    {
        MihomoServiceStatus status = MihomoServiceStatus.Unknown("Status not checked");

        StartupCheckItem item = StartupCheckService.BuildTransparentProxyCheck(
            transparentProxyEnabled: true,
            status,
            title: "Transparent proxy",
            disabledDescription: "Disabled",
            missingDescription: "Service missing",
            unknownDescription: "Status unknown");

        Assert.False(item.IsHealthy);
        Assert.Equal("Transparent proxy", item.Title);
        Assert.Equal("Status not checked", item.Description);
    }

    [Fact]
    public void BuildTransparentProxyCheck_WhenAbsenceIsConfirmed_UsesMissingDescription()
    {
        StartupCheckItem item = StartupCheckService.BuildTransparentProxyCheck(
            transparentProxyEnabled: true,
            new MihomoServiceStatus(false, false, "Not deployed"),
            title: "Transparent proxy",
            disabledDescription: "Disabled",
            missingDescription: "Service missing",
            unknownDescription: "Status unknown");

        Assert.False(item.IsHealthy);
        Assert.Equal("Service missing", item.Description);
    }

    [Fact]
    public void BuildTransparentProxyCheck_WhenStatusIsDefault_UsesUnknownDescription()
    {
        StartupCheckItem item = StartupCheckService.BuildTransparentProxyCheck(
            transparentProxyEnabled: true,
            default,
            title: "Transparent proxy",
            disabledDescription: "Disabled",
            missingDescription: "Service missing",
            unknownDescription: "Status unknown");

        Assert.False(item.IsHealthy);
        Assert.Equal("Status unknown", item.Description);
    }

    [Fact]
    public void Unknown_WhenMessageIsNull_RejectsInvalidStatus()
    {
        Assert.Throws<ArgumentNullException>(() => MihomoServiceStatus.Unknown(null!));
    }

    private static string GetString(string key)
    {
        return key switch
        {
            "StartupPrompt.Check.Subscription.Title" => "Proxy subscription",
            "StartupPrompt.Check.Subscription.Ready" => "Subscription ready",
            "StartupPrompt.Check.Subscription.Missing" => "Subscription missing",
            "StartupPrompt.Check.TransparentProxy.Title" => "Transparent proxy",
            "StartupPrompt.Check.TransparentProxy.Disabled" => "Disabled",
            "StartupPrompt.Check.TransparentProxy.Missing" => "Service missing",
            "MihomoService.Status.Unknown" => "Status unknown",
            "StartupPrompt.Check.Fallback.Title" => "Startup fallback",
            "StartupPrompt.Check.Fallback.Registered" => "Fallback registered",
            "StartupPrompt.Check.Fallback.NotRegistered" => "Fallback missing",
            "StartupPrompt.Check.StaleProxy.Title" => "Proxy residue",
            "StartupPrompt.Check.StaleProxy.Clean" => "Proxy clean",
            "StartupPrompt.Check.StaleProxy.Detected" => "Proxy residue detected",
            "StartupPrompt.Check.Unavailable" => "Check unavailable",
            _ => key,
        };
    }

    private sealed class RecordingErrorSink : IApplicationErrorSink
    {
        public List<ApplicationError> Errors { get; } = [];

        public Task ReportAsync(
            ApplicationError applicationError,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(applicationError);
            cancellationToken.ThrowIfCancellationRequested();
            Errors.Add(applicationError);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingErrorSink : IApplicationErrorSink
    {
        public int CallCount { get; private set; }

        public Task ReportAsync(
            ApplicationError applicationError,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("diagnostic sink unavailable");
        }
    }

    private sealed class FakeStartupCheckProbe : IStartupCheckProbe
    {
        public ManualResetEventSlim? SubscriptionStarted { get; init; }

        public ManualResetEventSlim? ReleaseSubscription { get; init; }

        public Exception? SubscriptionFailure { get; init; }

        public bool TransparentProxyEnabled { get; init; } = true;

        public int HasSubscriptionCalls { get; private set; }

        public int TransparentProxyEnabledCalls { get; private set; }

        public int MihomoStatusCalls { get; private set; }

        public int FallbackRegisteredCalls { get; private set; }

        public int WindowsProxyStateCalls { get; private set; }

        public int MixedPortCalls { get; private set; }

        public int StaleProxyCalls { get; private set; }

        public bool HasSubscription(CancellationToken cancellationToken)
        {
            HasSubscriptionCalls++;
            SubscriptionStarted?.Set();
            ReleaseSubscription?.Wait(cancellationToken);
            if (SubscriptionFailure is not null)
            {
                throw SubscriptionFailure;
            }

            return true;
        }

        public bool IsTransparentProxyEnabled(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransparentProxyEnabledCalls++;
            return TransparentProxyEnabled;
        }

        public MihomoServiceStatus GetMihomoStatus(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MihomoStatusCalls++;
            return new MihomoServiceStatus(true, true, "Running");
        }

        public bool IsFallbackRegistered(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FallbackRegisteredCalls++;
            return true;
        }

        public WindowsProxyState GetWindowsProxyState(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsProxyStateCalls++;
            return new WindowsProxyState(false, string.Empty);
        }

        public int GetMixedPort(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MixedPortCalls++;
            return 7890;
        }

        public bool IsStaleProxy(
            WindowsProxyState state,
            int mixedPort,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StaleProxyCalls++;
            return false;
        }
    }
}

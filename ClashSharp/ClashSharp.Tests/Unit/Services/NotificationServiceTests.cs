extern alias ClashSharpUi;
using ClashSharp.Model.Triggers;
using CompatibleNotificationPlatform = ClashSharpUi::ClashSharp.Service.CompatibleNotificationPlatform;
using ITriggerNotificationReceiptStore = ClashSharpUi::ClashSharp.Service.ITriggerNotificationReceiptStore;
using ITriggerRuntimeEventPublisher = ClashSharpUi::ClashSharp.Service.ITriggerRuntimeEventPublisher;
using IWin11NotificationPlatform = ClashSharpUi::ClashSharp.Service.IWin11NotificationPlatform;
using NotificationLevel = ClashSharp.Model.NotificationLevel;
using NotificationPayload = ClashSharpUi::ClashSharp.Service.NotificationPayload;
using NotificationService = ClashSharpUi::ClashSharp.Service.NotificationService;
using TriggerRuntimeEvent = ClashSharpUi::ClashSharp.Service.TriggerRuntimeEvent;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for notification policy and logging behavior.</summary>
public sealed class NotificationServiceTests
{
    [Theory]
    [InlineData(false, NotificationLevel.More, 0, 0, 0)]
    [InlineData(true, NotificationLevel.CriticalOnly, 0, 0, 1)]
    [InlineData(true, NotificationLevel.Default, 2, 1, 1)]
    [InlineData(true, NotificationLevel.More, 2, 2, 1)]
    public void VerifiedRoutingAndManagementOutcomesUseDistinctPolicies(bool enabled, NotificationLevel level,
        int routingCount, int managementCount, int importantCount)
    {
        FakeWin11NotificationPlatform routing = new();
        NotificationService service = CreateService(enabled, level, routing, new(), new());
        service.ReportOperation("Profiles", "Profiles.Status.Activated", false);
        service.ReportOperation("Nodes", "ProxyNodes.Status.SelectionApplied", false);
        Assert.Equal(routingCount, routing.Requests.Count);
        FakeWin11NotificationPlatform management = new();
        service = CreateService(enabled, level, management, new(), new());
        service.ReportOperation("Profiles", "Profiles.Status.Imported", false);
        service.ReportOperation("Profiles", "Profiles.Status.ImportFailed", true);
        Assert.Equal(managementCount, management.Requests.Count);
        FakeWin11NotificationPlatform important = new();
        service = CreateService(enabled, level, important, new(), new());
        service.ReportOperation("Runtime", "Notification.Startup.Attention", true);
        Assert.Equal(importantCount, important.Requests.Count);
    }

    [Theory]
    [InlineData(false, NotificationLevel.Default, 0)]
    [InlineData(true, NotificationLevel.CriticalOnly, 0)]
    [InlineData(true, NotificationLevel.Default, 1)]
    [InlineData(true, NotificationLevel.More, 1)]
    public void TransparentProxyStateChangeUsesTheStandardLevel(bool enabled, NotificationLevel level, int count)
    {
        FakeWin11NotificationPlatform platform = new();
        NotificationService service = CreateService(enabled, level, platform, new(), new());

        service.NotifyTransparentProxyChanged(true, false);

        Assert.Equal(count, platform.Requests.Count);
        if (count != 0)
        {
            Assert.Contains("Pending", platform.Requests[0].Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompatibilityTransportKeepsRegistrationAndReceiptsOnTheSelectedPlatform(bool supported)
    {
        bool currentSupport = supported;
        Transport modern = new();
        Transport legacy = new();
        using CompatibleNotificationPlatform platform = new(() => currentSupport, modern, legacy);
        int activated = 0;
        using IDisposable registration = platform.Register(() => activated++);
        currentSupport = !supported;
        platform.Show("Title", "Message", "stable-receipt");
        Assert.True(await platform.ContainsAsync("stable-receipt", CancellationToken.None));
        Transport selected = supported ? modern : legacy;
        Transport unused = supported ? legacy : modern;
        selected.Activated!();
        Assert.Equal(1, activated);
        Assert.Equal(1, selected.Shows);
        Assert.Equal(0, unused.Shows);
        Assert.Null(unused.Activated);
        registration.Dispose();
        Assert.Null(selected.Activated);
    }

    [Fact]
    public void SupportedTransportFailureDoesNotBypassWindowsSettingsThroughAnotherTransport()
    {
        FakeWin11NotificationPlatform modern = new() { ExceptionToThrow = new InvalidOperationException("blocked") };
        FakeWin11NotificationPlatform fallback = new();
        using CompatibleNotificationPlatform platform = new(() => true, modern, fallback);
        Assert.Throws<InvalidOperationException>(() => platform.Show("Title", "Message"));
        Assert.Empty(fallback.Requests);
    }

    [Fact]
    public void CompatibleToastTreatsMessageContentAsText()
    {
        const string title = "Import <configuration> & test";
        const string message = "\"quoted\" </text><actions>用户配置";
        System.Xml.Linq.XElement payload = System.Xml.Linq.XElement.Parse(NotificationPayload.Create(title, message));
        Assert.Equal(new[] { title, message }, payload.Descendants("text").Select(element => element.Value));
        Assert.Empty(payload.Descendants("actions"));
        Assert.Equal("ToastGeneric", (string?)payload.Element("visual")?.Element("binding")?.Attribute("template"));
    }

    private sealed class Transport : IWin11NotificationPlatform, ClashSharpUi::ClashSharp.Service.IWin11NotificationRegistration
    {
        public Action? Activated { get; private set; }
        public int Shows { get; private set; }
        private string? _receipt;
        public IDisposable Register(Action activated)
        {
            Activated = activated;
            return new ClashSharpUi::ClashSharp.Service.NotificationRegistration(() => Activated = null);
        }
        public Task<bool> ContainsAsync(string idempotencyKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_receipt == idempotencyKey);
        }
        public void Show(string title, string message, string? idempotencyKey = null)
        {
            Shows++;
            _receipt = idempotencyKey;
        }
    }

    [Theory]
    [InlineData(NotificationLevel.CriticalOnly, false, false)]
    [InlineData(NotificationLevel.Default, false, false)]
    [InlineData(NotificationLevel.More, false, true)]
    [InlineData(NotificationLevel.CriticalOnly, true, false)]
    [InlineData(NotificationLevel.Default, true, true)]
    [InlineData(NotificationLevel.More, true, true)]
    public void OperationOutcomesRespectAllVerbosityLevels(NotificationLevel level, bool failed, bool expected)
    {
        FakeWin11NotificationPlatform platform = new();
        NotificationService service = CreateService(true, level, platform, new(), new());
        service.ReportOperation("Title", "Message", failed);
        Assert.Equal(expected ? 1 : 0, platform.Requests.Count);
    }

    [Theory]
    [InlineData(NotificationLevel.CriticalOnly)]
    [InlineData(NotificationLevel.Default)]
    [InlineData(NotificationLevel.More)]
    public void DisabledNotificationsSuppressEveryOperationAndAvailabilityOutcome(NotificationLevel level)
    {
        FakeWin11NotificationPlatform platform = new();
        NotificationService service = CreateService(false, level, platform, new(), new());
        service.ReportOperation("Title", "Succeeded", false);
        service.ReportOperation("Title", "Failed", true);
        service.ReportAvailability("Title", "Unavailable", true);
        service.ReportAvailability("Title", "Recovered", false);
        service.NotifyConnectionTestTimeout("First target");
        Assert.Empty(platform.Requests);
    }

    [Theory]
    [InlineData(NotificationLevel.CriticalOnly, false)]
    [InlineData(NotificationLevel.Default, true)]
    [InlineData(NotificationLevel.More, true)]
    public void TestTimeoutRequiresDefaultOrDetailedVerbosity(NotificationLevel level, bool expected)
    {
        FakeWin11NotificationPlatform platform = new();
        NotificationService service = CreateService(true, level, platform, new(), new());
        service.NotifyConnectionTestTimeout("First target");
        Assert.Equal(expected ? 1 : 0, platform.Requests.Count);
    }

    [Theory]
    [InlineData(NotificationLevel.CriticalOnly, true, true)]
    [InlineData(NotificationLevel.Default, true, true)]
    [InlineData(NotificationLevel.More, true, true)]
    [InlineData(NotificationLevel.CriticalOnly, false, false)]
    [InlineData(NotificationLevel.Default, false, true)]
    [InlineData(NotificationLevel.More, false, true)]
    public void AvailabilityFailureAndRecoveryUseImportantAndDefaultPolicies(NotificationLevel level, bool failed, bool expected)
    {
        FakeWin11NotificationPlatform platform = new();
        NotificationService service = CreateService(true, level, platform, new(), new());
        service.ReportAvailability("Title", "Message", failed);
        Assert.Equal(expected ? 1 : 0, platform.Requests.Count);
    }

    [Fact]
    public void OwnedNotificationRegistrationIsReleasedExactlyOnce()
    {
        int released = 0;
        using ClashSharpUi::ClashSharp.Service.NotificationRegistration registration = new(() => released++);
        registration.Dispose();
        registration.Dispose();
        Assert.Equal(1, released);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void AvailabilityFailureUsesOnlyPreviouslyObservedPolicyDuringSettingsRecovery(bool enabled, int expected)
    {
        bool blocked = false;
        FakeWin11NotificationPlatform platform = new();
        NotificationService service = new(() => blocked ? throw new InvalidOperationException("settings admission closed") : enabled,
            () => NotificationLevel.CriticalOnly, key => key, static (_, _, _, _) => { }, new FakeTriggerRuntimeEvents(), platform);
        service.ReportOperation("Title", "Warm policy", false);
        Assert.Empty(platform.Requests);
        blocked = true;
        service.ReportAvailability("Title", "Recovery required", true);
        Assert.Equal(expected, platform.Requests.Count);
    }

    [Fact]
    public void UnavailableSettingsDoNotInventNotificationPermission()
    {
        FakeWin11NotificationPlatform platform = new();
        NotificationService service = new(() => throw new InvalidOperationException("settings unavailable"),
            () => NotificationLevel.More, key => key, static (_, _, _, _) => { }, new FakeTriggerRuntimeEvents(), platform);
        service.ReportAvailability("Title", "Recovery required", true);
        Assert.Empty(platform.Requests);
    }

    [Fact]
    public void Show_WhenNotificationIsSuppressed_LogsSuppressionWithoutPlatformCallOrRuntimeEvent()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerRuntimeEvents triggerEvents = new();
        FakeNotificationLog log = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.CriticalOnly,
            platform,
            triggerEvents,
            log);

        service.Show(NotificationLevel.Default, "Title", "Message");

        Assert.Empty(platform.Requests);
        Assert.Empty(triggerEvents.Events);
        LogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Info", entry.Level);
        Assert.Equal("Notification", entry.Category);
        Assert.Equal("Suppressed Title Message", entry.Message);
        Assert.Contains("Title: Title", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_WhenNotificationIsShown_LogsSuccessAndPublishesNotificationRaisedEvent()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerRuntimeEvents triggerEvents = new();
        FakeNotificationLog log = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            triggerEvents,
            log);

        service.Show(NotificationLevel.CriticalOnly, "Critical", "Body");

        Assert.Equal([new NotificationRequest("Critical", "Body")], platform.Requests);
        TriggerRuntimeEvent triggerEvent = Assert.Single(triggerEvents.Events);
        Assert.Equal(TriggerEventKind.NotificationRaised, triggerEvent.EventKind);
        Assert.Equal(NotificationLevel.CriticalOnly, triggerEvent.NotificationLevel);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Message == "Shown Critical Body");
    }

    [Fact]
    public void Show_WhenWin11NotificationFails_LogsWarningWithoutPublishingRuntimeEvent()
    {
        FakeWin11NotificationPlatform platform = new()
        {
            ExceptionToThrow = new InvalidOperationException("toast unavailable"),
        };
        FakeTriggerRuntimeEvents triggerEvents = new();
        FakeNotificationLog log = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.More,
            platform,
            triggerEvents,
            log);

        service.Show(NotificationLevel.Default, "Title", "Message");

        Assert.Empty(triggerEvents.Events);
        LogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("Failed Title Message toast unavailable", entry.Message);
        Assert.Contains("Error: toast unavailable", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_WhenWin11PlatformThrowsUnexpectedException_ContainsFailureWithoutTerminatingCaller()
    {
        FakeWin11NotificationPlatform platform = new()
        {
            ExceptionToThrow = new IOException("toast registration unavailable"),
        };
        FakeTriggerRuntimeEvents triggerEvents = new();
        FakeNotificationLog log = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            triggerEvents,
            log);

        service.Show(NotificationLevel.Default, "Title", "Message");

        Assert.Empty(triggerEvents.Events);
        LogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Warning", entry.Level);
        Assert.Contains("toast registration unavailable", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_WhenWin11PlatformThrowsProcessFatalException_DoesNotSwallowFailure()
    {
        OutOfMemoryException expected = Assert.IsType<OutOfMemoryException>(
            Activator.CreateInstance(
                typeof(OutOfMemoryException),
                "process memory exhausted"));
        FakeWin11NotificationPlatform platform = new()
        {
            ExceptionToThrow = expected,
        };
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            new FakeNotificationLog());

        OutOfMemoryException actual = Assert.Throws<OutOfMemoryException>(
            () => service.Show(NotificationLevel.Default, "Title", "Message"));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void Show_WhenWin11PlatformWrapsProcessFatalException_DoesNotSwallowFailure()
    {
        OutOfMemoryException processFailure = Assert.IsType<OutOfMemoryException>(
            Activator.CreateInstance(
                typeof(OutOfMemoryException),
                "process memory exhausted"));
        AggregateException expected = new(
            "notification platform failed",
            new InvalidOperationException(
                "native notification call failed",
                processFailure));
        FakeWin11NotificationPlatform platform = new()
        {
            ExceptionToThrow = expected,
        };
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            new FakeNotificationLog());

        AggregateException actual = Assert.Throws<AggregateException>(
            () => service.Show(NotificationLevel.Default, "Title", "Message"));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void ReportTriggerFiredNotificationFailure_WhenDiagnosticLogThrowsProcessFatalException_DoesNotSwallowFailure()
    {
        OutOfMemoryException expected = Assert.IsType<OutOfMemoryException>(
            Activator.CreateInstance(
                typeof(OutOfMemoryException),
                "process memory exhausted"));
        FakeNotificationLog log = new()
        {
            ExceptionToThrow = expected,
        };
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            new FakeWin11NotificationPlatform(),
            new FakeTriggerRuntimeEvents(),
            log);

        OutOfMemoryException actual = Assert.Throws<OutOfMemoryException>(
            () => service.ReportTriggerFiredNotificationFailure(
                "Alpha",
                new IOException("notification delivery failed")));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void Show_WhenRuntimeEventPublisherThrowsUnexpectedException_DoesNotHideProgrammingFailure()
    {
        IOException expected = new("runtime event persistence failed");
        FakeTriggerRuntimeEvents triggerEvents = new()
        {
            ExceptionToThrow = expected,
        };
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            new FakeWin11NotificationPlatform(),
            triggerEvents,
            new FakeNotificationLog());

        IOException actual = Assert.Throws<IOException>(
            () => service.Show(NotificationLevel.Default, "Title", "Message"));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void Show_WhenRuntimeEventPublisherRejectsExpectedOperation_LogsAndContainsFailure()
    {
        FakeTriggerRuntimeEvents triggerEvents = new()
        {
            ExceptionToThrow = new InvalidOperationException("publisher unavailable"),
        };
        FakeNotificationLog log = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            new FakeWin11NotificationPlatform(),
            triggerEvents,
            log);

        service.Show(NotificationLevel.Default, "Title", "Message");

        Assert.Contains(
            log.Entries,
            entry => entry.Level == "Warning"
                && entry.Message.Contains("publisher unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeliverTriggerNotificationAsync_RepeatedIdentity_HasOneEffectiveDelivery()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerRuntimeEvents triggerEvents = new();
        FakeNotificationLog log = new();
        FakeTriggerNotificationReceiptStore receipts = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            triggerEvents,
            log,
            receipts);

        await service.DeliverTriggerNotificationAsync("execution:1:0", "Body", CancellationToken.None);
        await service.DeliverTriggerNotificationAsync("execution:1:0", "Body", CancellationToken.None);

        NotificationRequest request = Assert.Single(platform.Requests);
        Assert.Equal("execution:1:0", request.IdempotencyKey);
        Assert.True(await service.IsTriggerNotificationDeliveredAsync(
            "execution:1:0",
            CancellationToken.None));
        Assert.Empty(triggerEvents.Events);
    }

    [Fact]
    public async Task DeliverTriggerNotificationAsync_Suppressed_IsDurablyCompleteWithoutPlatformCall()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerNotificationReceiptStore receipts = new();
        NotificationService service = CreateService(
            enabled: false,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            new FakeNotificationLog(),
            receipts);

        await service.DeliverTriggerNotificationAsync("execution:2:0", "Body", CancellationToken.None);

        Assert.Empty(platform.Requests);
        Assert.True(receipts.Contains("execution:2:0"));
    }

    [Fact]
    public async Task DeliverTriggerFiredNotificationAsync_RepeatedExecutionHasOneFiredDelivery()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerNotificationReceiptStore receipts = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            new FakeNotificationLog(),
            receipts);

        await service.DeliverTriggerFiredNotificationAsync(
            "trigger-fired:execution-1",
            "Alpha",
            isTriggerNotificationEnabled: true,
            CancellationToken.None);
        await service.DeliverTriggerFiredNotificationAsync(
            "trigger-fired:execution-1",
            "Alpha",
            isTriggerNotificationEnabled: true,
            CancellationToken.None);

        NotificationRequest request = Assert.Single(platform.Requests);
        Assert.Equal("Trigger", request.Title);
        Assert.Equal("Trigger Alpha", request.Message);
        Assert.Equal("trigger-fired:execution-1", request.IdempotencyKey);
    }

    [Fact]
    public async Task DeliverTriggerFiredNotificationAsync_DisabledFeatureIsDurablySuppressed()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerNotificationReceiptStore receipts = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            new FakeNotificationLog(),
            receipts);

        await service.DeliverTriggerFiredNotificationAsync(
            "trigger-fired:execution-2",
            "Alpha",
            isTriggerNotificationEnabled: false,
            CancellationToken.None);

        Assert.Empty(platform.Requests);
        Assert.True(receipts.Contains("trigger-fired:execution-2"));
    }

    [Fact]
    public async Task DeliverTriggerFiredNotificationAsync_DisabledFeatureSkipsPlatformLookup()
    {
        FakeWin11NotificationPlatform platform = new()
        {
            ContainsException = new IOException("platform lookup failed"),
        };
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            new FakeNotificationLog(),
            new FakeTriggerNotificationReceiptStore());

        await service.DeliverTriggerFiredNotificationAsync(
            "trigger-fired:execution-disabled",
            "Alpha",
            isTriggerNotificationEnabled: false,
            CancellationToken.None);

        Assert.Empty(platform.Requests);
    }

    [Theory]
    [InlineData(FiredNotificationFault.PlatformContains)]
    [InlineData(FiredNotificationFault.PlatformShow)]
    [InlineData(FiredNotificationFault.ReceiptContains)]
    [InlineData(FiredNotificationFault.ReceiptRecord)]
    [InlineData(FiredNotificationFault.Log)]
    public async Task FiredNotificationInfrastructureFailure_IsSafelyReportable(
        FiredNotificationFault fault)
    {
        IOException failure = new($"{fault} failed");
        FakeWin11NotificationPlatform platform = new()
        {
            ContainsException = fault == FiredNotificationFault.PlatformContains
                ? failure
                : null,
            ExceptionToThrow = fault == FiredNotificationFault.PlatformShow
                ? failure
                : null,
        };
        FakeTriggerNotificationReceiptStore receipts = new()
        {
            ContainsException = fault == FiredNotificationFault.ReceiptContains
                ? failure
                : null,
            RecordException = fault == FiredNotificationFault.ReceiptRecord
                ? failure
                : null,
        };
        FakeNotificationLog log = new()
        {
            ExceptionToThrow = fault == FiredNotificationFault.Log
                ? failure
                : null,
        };
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            log,
            receipts);

        Exception observed = await Assert.ThrowsAnyAsync<Exception>(
            () => service.DeliverTriggerFiredNotificationAsync(
                $"trigger-fired:{fault}",
                "Alpha",
                isTriggerNotificationEnabled: true,
                CancellationToken.None));

        Assert.Same(failure, observed);
        service.ReportTriggerFiredNotificationFailure("Alpha", observed);
        if (fault != FiredNotificationFault.Log)
        {
            Assert.Contains(log.Entries, entry => entry.Level == "Warning");
        }
    }

    [Fact]
    public async Task IsTriggerNotificationDeliveredAsync_AfterReceiptCommitCrash_RecoversFromPlatformIdentity()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerNotificationReceiptStore failingReceipts = new()
        {
            RecordException = new IOException("receipt write failed"),
        };
        NotificationService interrupted = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            new FakeNotificationLog(),
            failingReceipts);

        await Assert.ThrowsAsync<IOException>(() => interrupted.DeliverTriggerNotificationAsync(
            "execution:3:0",
            "Body",
            CancellationToken.None));

        FakeTriggerNotificationReceiptStore recoveredReceipts = new();
        NotificationService recovered = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            new FakeTriggerRuntimeEvents(),
            new FakeNotificationLog(),
            recoveredReceipts);
        Assert.True(await recovered.IsTriggerNotificationDeliveredAsync(
            "execution:3:0",
            CancellationToken.None));
        await recovered.DeliverTriggerNotificationAsync("execution:3:0", "Body", CancellationToken.None);

        Assert.Single(platform.Requests);
        Assert.True(recoveredReceipts.Contains("execution:3:0"));
    }

    private static NotificationService CreateService(
        bool enabled,
        NotificationLevel configuredLevel,
        FakeWin11NotificationPlatform platform,
        FakeTriggerRuntimeEvents triggerEvents,
        FakeNotificationLog log,
        ITriggerNotificationReceiptStore? receiptStore = null)
    {
        return new NotificationService(
            () => enabled,
            () => configuredLevel,
            key => key switch
            {
                "Notification.Log.Suppressed" => "Suppressed {0} {1}",
                "Notification.Log.Shown" => "Shown {0} {1}",
                "Notification.Log.Failed" => "Failed {0} {1} {2}",
                "Notification.Custom.Title" => "Custom",
                "Notification.Custom.Message" => "Default message",
                "Notification.ProxyMode.Title" => "Proxy mode",
                "Notification.ProxyMode.Message.Format" => "Mode {0}",
                "Notification.TriggerFired.Title" => "Trigger",
                "Notification.TriggerFired.Message.Format" => "Trigger {0}",
                "Notification.ConnectionTestTimeout.Title" => "Timeout",
                "Notification.ConnectionTestTimeout.Message.Format" => "Timeout {0}",
                "Master.Mode.Standby.Title" => "Standby",
                "Master.Mode.RuleTakeover.Title" => "Rule",
                "Master.Mode.FullTakeover.Title" => "Full",
                "Master.Mode.Disabled.Title" => "Disabled",
                _ => key,
            },
            log.Append,
            triggerEvents,
            platform,
            receiptStore);
    }

    private sealed class FakeWin11NotificationPlatform : IWin11NotificationPlatform
    {
        public List<NotificationRequest> Requests { get; } = [];

        public Exception? ContainsException { get; init; }

        public Exception? ExceptionToThrow { get; init; }

        public Task<bool> ContainsAsync(string idempotencyKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContainsException is not null)
            {
                throw ContainsException;
            }

            return Task.FromResult(Requests.Any(request =>
                StringComparer.Ordinal.Equals(request.IdempotencyKey, idempotencyKey)));
        }

        public void Show(string title, string message, string? idempotencyKey = null)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            Requests.RemoveAll(request =>
                idempotencyKey is not null
                && StringComparer.Ordinal.Equals(request.IdempotencyKey, idempotencyKey));
            Requests.Add(new NotificationRequest(title, message, idempotencyKey));
        }
    }

    private sealed class FakeTriggerNotificationReceiptStore : ITriggerNotificationReceiptStore
    {
        private readonly HashSet<string> _receipts = new(StringComparer.Ordinal);

        public Exception? ContainsException { get; init; }

        public Exception? RecordException { get; init; }

        public bool Contains(string idempotencyKey)
        {
            if (ContainsException is not null)
            {
                throw ContainsException;
            }

            return _receipts.Contains(idempotencyKey);
        }

        public void Record(string idempotencyKey)
        {
            if (RecordException is not null)
            {
                throw RecordException;
            }

            _receipts.Add(idempotencyKey);
        }
    }

    private sealed class FakeTriggerRuntimeEvents : ITriggerRuntimeEventPublisher
    {
        public List<TriggerRuntimeEvent> Events { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public void Publish(TriggerRuntimeEvent triggerEvent)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            Events.Add(triggerEvent);
        }
    }

    private sealed class FakeNotificationLog
    {
        public List<LogEntry> Entries { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public void Append(string level, string category, string message, string? detail)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            Entries.Add(new LogEntry(level, category, message, detail ?? string.Empty));
        }
    }

    public enum FiredNotificationFault
    {
        PlatformContains,
        PlatformShow,
        ReceiptContains,
        ReceiptRecord,
        Log,
    }

    private sealed record NotificationRequest(string Title, string Message, string? IdempotencyKey = null);

    private sealed record LogEntry(string Level, string Category, string Message, string Detail);
}

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for tray command coordination.</summary>
public sealed class TrayCommandServiceTests
{
    /// <summary>Verifies tray mode commands apply and persist the resulting mode.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTakeoverSucceeds_LeavesPersistenceToCoordinatorAndLogs()
    {
        FakeTraySettings settings = new() { CurrentMode = ClashSharpMode.Disabled };
        FakeTrayTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(ClashSharpMode.FullTakeover, true, true, false, "applied"),
        };
        FakeTrayLog log = new();
        TrayCommandService service = CreateService(settings, takeover: takeover, log: log);

        await service.ApplyModeAsync(ClashSharpMode.FullTakeover, CancellationToken.None);

        Assert.Equal(ClashSharpMode.Disabled, settings.CurrentMode);
        Assert.Equal([ClashSharpMode.FullTakeover], takeover.AppliedModes);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Category == "Tray" && entry.Message == "applied");
    }

    /// <summary>Verifies changing transparent proxy records intent without claiming a runtime transition.</summary>
    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public async Task SetTransparentProxyEnabledAsync_WhenTakeoverModeIsActive_DoesNotApplyRuntime(ClashSharpMode currentMode)
    {
        FakeTraySettings settings = new()
        {
            CurrentMode = currentMode,
            TransparentProxyEnabled = true,
        };
        FakeTrayTakeover takeover = new();
        FakeTrayLog log = new();
        TrayCommandService service = CreateService(settings, takeover: takeover, log: log);

        bool modeApplied = await service.SetTransparentProxyEnabledAsync(false, CancellationToken.None);

        Assert.False(settings.TransparentProxyEnabled);
        Assert.Empty(takeover.AppliedModes);
        Assert.Empty(log.Entries);
        Assert.False(modeApplied);
    }

    /// <summary>Verifies changing transparent proxy outside active takeover modes only changes the preference.</summary>
    [Theory]
    [InlineData(ClashSharpMode.Disabled)]
    [InlineData(ClashSharpMode.Standby)]
    public async Task SetTransparentProxyEnabledAsync_WhenTakeoverModeIsInactive_DoesNotApplyMode(ClashSharpMode currentMode)
    {
        FakeTraySettings settings = new()
        {
            CurrentMode = currentMode,
            TransparentProxyEnabled = true,
        };
        FakeTrayTakeover takeover = new();
        TrayCommandService service = CreateService(settings, takeover: takeover);

        await service.SetTransparentProxyEnabledAsync(false, CancellationToken.None);

        Assert.False(settings.TransparentProxyEnabled);
        Assert.Empty(takeover.AppliedModes);
    }

    /// <summary>Verifies enabling transparent proxy records the preference without applying a mode.</summary>
    [Fact]
    public async Task SetTransparentProxyEnabledAsync_RecordsPreferenceWithoutApplyingMode()
    {
        FakeTraySettings settings = new()
        {
            CurrentMode = ClashSharpMode.RuleTakeover,
            TransparentProxyEnabled = false,
        };
        FakeTrayTakeover takeover = new();
        TrayCommandService service = CreateService(settings, takeover);

        bool modeApplied = await service.SetTransparentProxyEnabledAsync(true, CancellationToken.None);

        Assert.True(settings.TransparentProxyEnabled);
        Assert.Empty(takeover.AppliedModes);
        Assert.False(modeApplied);
    }

    /// <summary>Verifies failed tray mode application reports failure to callers that own notifications.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTakeoverFails_ReturnsFalse()
    {
        FakeTrayTakeover takeover = new()
        {
            ExceptionToThrow = new InvalidOperationException("missing core"),
        };
        TrayCommandService service = CreateService(takeover: takeover);

        bool modeApplied = await service.ApplyModeAsync(ClashSharpMode.FullTakeover, CancellationToken.None);

        Assert.False(modeApplied);
    }

    /// <summary>Creates a tray command service with test doubles.</summary>
    private static TrayCommandService CreateService(
        FakeTraySettings? settings = null,
        FakeTrayTakeover? takeover = null,
        FakeTrayLog? log = null)
    {
        return new TrayCommandService(
            settings ?? new FakeTraySettings(),
            takeover ?? new FakeTrayTakeover(),
            log ?? new FakeTrayLog());
    }

    /// <summary>Fake settings store for tray command tests.</summary>
    private sealed class FakeTraySettings : ITrayCommandSettings
    {
        public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;

        public bool TransparentProxyEnabled { get; set; }
    }

    /// <summary>Fake takeover service for tray command tests.</summary>
    private sealed class FakeTrayTakeover : ITrayCommandTakeover
    {
        public NetworkTakeoverResult Result { get; set; } = new(ClashSharpMode.Disabled, false, false, false, "applied");

        public Exception? ExceptionToThrow { get; set; }

        public List<ClashSharpMode> AppliedModes { get; } = [];

        public Task<NetworkTakeoverResult> ApplyModeAsync(ClashSharpMode mode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppliedModes.Add(mode);
            NetworkTakeoverResult result = ExceptionToThrow is null
                ? Result with { Mode = mode }
                : throw ExceptionToThrow;
            return Task.FromResult(result);
        }
    }

    /// <summary>Fake log sink for tray command tests.</summary>
    private sealed class FakeTrayLog : ITrayCommandLog
    {
        public List<LogEntry> Entries { get; } = [];

        public void Append(string level, string category, string message, string? detail)
        {
            Entries.Add(new LogEntry(level, category, message, detail));
        }
    }

    /// <summary>Captured log entry.</summary>
    private sealed record LogEntry(string Level, string Category, string Message, string? Detail);
}

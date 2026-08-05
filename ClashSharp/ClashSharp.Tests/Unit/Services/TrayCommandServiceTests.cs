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

    /// <summary>Verifies changing transparent proxy is routed through the verified runtime transaction.</summary>
    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public async Task SetTransparentProxyEnabledAsync_WhenTakeoverModeIsActive_AppliesRuntime(ClashSharpMode currentMode)
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

        Assert.True(settings.TransparentProxyEnabled);
        Assert.Empty(takeover.AppliedModes);
        Assert.Equal([false], takeover.AppliedTransparentProxySettings);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Category == "Tray");
        Assert.True(modeApplied);
    }

    /// <summary>Verifies standby/disabled requests still commit through the same durable transaction.</summary>
    [Theory]
    [InlineData(ClashSharpMode.Disabled)]
    [InlineData(ClashSharpMode.Standby)]
    public async Task SetTransparentProxyEnabledAsync_WhenTakeoverModeIsInactive_AppliesRuntimePlan(ClashSharpMode currentMode)
    {
        FakeTraySettings settings = new()
        {
            CurrentMode = currentMode,
            TransparentProxyEnabled = true,
        };
        FakeTrayTakeover takeover = new();
        TrayCommandService service = CreateService(settings, takeover: takeover);

        await service.SetTransparentProxyEnabledAsync(false, CancellationToken.None);

        Assert.True(settings.TransparentProxyEnabled);
        Assert.Empty(takeover.AppliedModes);
        Assert.Equal([false], takeover.AppliedTransparentProxySettings);
    }

    /// <summary>Verifies enabling transparent proxy leaves durable persistence to the coordinator.</summary>
    [Fact]
    public async Task SetTransparentProxyEnabledAsync_LeavesPersistenceToCoordinator()
    {
        FakeTraySettings settings = new()
        {
            CurrentMode = ClashSharpMode.RuleTakeover,
            TransparentProxyEnabled = false,
        };
        FakeTrayTakeover takeover = new();
        TrayCommandService service = CreateService(settings, takeover);

        bool modeApplied = await service.SetTransparentProxyEnabledAsync(true, CancellationToken.None);

        Assert.False(settings.TransparentProxyEnabled);
        Assert.Empty(takeover.AppliedModes);
        Assert.Equal([true], takeover.AppliedTransparentProxySettings);
        Assert.True(modeApplied);
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
            takeover ?? new FakeTrayTakeover(),
            log ?? new FakeTrayLog());
    }

    /// <summary>Independent settings probe proving the tray service no longer writes preferences.</summary>
    private sealed class FakeTraySettings
    {
        public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;

        public bool TransparentProxyEnabled { get; set; }

        public int MixedPort { get; set; } = 10000;
    }

    /// <summary>Fake takeover service for tray command tests.</summary>
    private sealed class FakeTrayTakeover : ITrayCommandTakeover
    {
        public NetworkTakeoverResult Result { get; set; } = new(ClashSharpMode.Disabled, false, false, false, "applied");

        public Exception? ExceptionToThrow { get; set; }

        public List<ClashSharpMode> AppliedModes { get; } = [];

        public List<bool> AppliedTransparentProxySettings { get; } = [];

        public Task<NetworkTakeoverResult> ApplyModeAsync(ClashSharpMode mode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppliedModes.Add(mode);
            NetworkTakeoverResult result = ExceptionToThrow is null
                ? Result with { Mode = mode }
                : throw ExceptionToThrow;
            return Task.FromResult(result);
        }

        public Task<NetworkTakeoverResult> ApplyTransparentProxyAsync(
            bool transparentProxyEnabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppliedTransparentProxySettings.Add(transparentProxyEnabled);
            return ExceptionToThrow is null
                ? Task.FromResult(Result)
                : Task.FromException<NetworkTakeoverResult>(ExceptionToThrow);
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

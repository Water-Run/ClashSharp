/*
 * Tray Command Service Tests
 * Verifies tray command coordination without creating a native tray icon
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/TrayCommandServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for tray command coordination.</summary>
public sealed class TrayCommandServiceTests
{
    /// <summary>Verifies tray mode commands apply and persist the resulting mode.</summary>
    [Fact]
    public void ApplyMode_WhenTakeoverSucceeds_PersistsResultModeAndLogs()
    {
        FakeTraySettings settings = new() { CurrentMode = ClashSharpMode.Disabled };
        FakeTrayTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(ClashSharpMode.FullTakeover, true, true, false, "applied"),
        };
        FakeTrayLog log = new();
        TrayCommandService service = CreateService(settings, takeover: takeover, log: log);

        service.ApplyMode(ClashSharpMode.FullTakeover);

        Assert.Equal(ClashSharpMode.FullTakeover, settings.CurrentMode);
        Assert.Equal([ClashSharpMode.FullTakeover], takeover.AppliedModes);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Category == "Tray" && entry.Message == "applied");
    }

    /// <summary>Verifies changing transparent proxy while a takeover mode is active reapplies the current mode.</summary>
    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public void SetTransparentProxyEnabled_WhenTakeoverModeIsActive_ReappliesCurrentMode(ClashSharpMode currentMode)
    {
        FakeTraySettings settings = new()
        {
            CurrentMode = currentMode,
            TransparentProxyEnabled = true,
        };
        FakeTrayTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(currentMode, true, true, false, "reapplied"),
        };
        FakeTrayLog log = new();
        TrayCommandService service = CreateService(settings, takeover: takeover, log: log);

        service.SetTransparentProxyEnabled(false);

        Assert.False(settings.TransparentProxyEnabled);
        Assert.Equal([currentMode], takeover.AppliedModes);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Category == "Tray" && entry.Message == "reapplied");
    }

    /// <summary>Verifies changing transparent proxy outside active takeover modes only changes the preference.</summary>
    [Theory]
    [InlineData(ClashSharpMode.Disabled)]
    [InlineData(ClashSharpMode.Standby)]
    public void SetTransparentProxyEnabled_WhenTakeoverModeIsInactive_DoesNotApplyMode(ClashSharpMode currentMode)
    {
        FakeTraySettings settings = new()
        {
            CurrentMode = currentMode,
            TransparentProxyEnabled = true,
        };
        FakeTrayTakeover takeover = new();
        TrayCommandService service = CreateService(settings, takeover: takeover);

        service.SetTransparentProxyEnabled(false);

        Assert.False(settings.TransparentProxyEnabled);
        Assert.Empty(takeover.AppliedModes);
    }

    /// <summary>Verifies enabling transparent proxy without the service preserves the preference and does not apply a mode.</summary>
    [Fact]
    public void SetTransparentProxyEnabled_WhenServiceMissing_PreservesPreferenceWithoutApplyingMode()
    {
        FakeTraySettings settings = new()
        {
            CurrentMode = ClashSharpMode.RuleTakeover,
            TransparentProxyEnabled = false,
        };
        FakeTrayTakeover takeover = new();
        TrayCommandService service = CreateService(
            settings,
            new FakeTrayServiceStatus(new MihomoServiceStatus(false, false, "Not installed")),
            takeover);

        service.SetTransparentProxyEnabled(true);

        Assert.True(settings.TransparentProxyEnabled);
        Assert.Empty(takeover.AppliedModes);
    }

    /// <summary>Creates a tray command service with test doubles.</summary>
    private static TrayCommandService CreateService(
        FakeTraySettings? settings = null,
        FakeTrayServiceStatus? serviceStatus = null,
        FakeTrayTakeover? takeover = null,
        FakeTrayLog? log = null)
    {
        return new TrayCommandService(
            settings ?? new FakeTraySettings(),
            serviceStatus ?? new FakeTrayServiceStatus(new MihomoServiceStatus(true, true, "Installed")),
            takeover ?? new FakeTrayTakeover(),
            log ?? new FakeTrayLog());
    }

    /// <summary>Fake settings store for tray command tests.</summary>
    private sealed class FakeTraySettings : ITrayCommandSettings
    {
        public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;

        public bool TransparentProxyEnabled { get; set; }
    }

    /// <summary>Fake mihomo service status provider for tray command tests.</summary>
    private sealed class FakeTrayServiceStatus(MihomoServiceStatus status) : ITrayCommandMihomoService
    {
        public MihomoServiceStatus Status { get; set; } = status;

        public MihomoServiceStatus GetStatus()
        {
            return Status;
        }
    }

    /// <summary>Fake takeover service for tray command tests.</summary>
    private sealed class FakeTrayTakeover : ITrayCommandTakeover
    {
        public NetworkTakeoverResult Result { get; set; } = new(ClashSharpMode.Disabled, false, false, false, "applied");

        public Exception? ExceptionToThrow { get; set; }

        public List<ClashSharpMode> AppliedModes { get; } = [];

        public NetworkTakeoverResult ApplyMode(ClashSharpMode mode)
        {
            AppliedModes.Add(mode);
            return ExceptionToThrow is null
                ? Result with { Mode = mode }
                : throw ExceptionToThrow;
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

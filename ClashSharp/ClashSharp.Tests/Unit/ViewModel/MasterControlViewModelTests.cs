/*
 * Master Control ViewModel Tests
 * Verifies master control status and takeover mode behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/MasterControlViewModelTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the master control view model.</summary>
public sealed class MasterControlViewModelTests
{
    /// <summary>Verifies construction loads localized static labels and persisted mode state.</summary>
    [Fact]
    public void Constructor_LoadsLocalizedLabelsAndSelectedMode()
    {
        FakeMasterSettings settings = new() { CurrentMode = ClashSharpMode.RuleTakeover };

        MasterControlViewModel viewModel = CreateViewModel(settings: settings);

        Assert.Equal("Master", viewModel.PageTitleText);
        Assert.Equal("Status title", viewModel.StatusControlTitleText);
        Assert.True(viewModel.IsRuleTakeoverModeSelected);
        Assert.False(viewModel.IsDisabledModeSelected);
    }

    /// <summary>Verifies loading probes core version and visible proxy status.</summary>
    [Fact]
    public async Task LoadAsync_WhenServicesSucceed_UpdatesStatusText()
    {
        FakeMasterCore core = new() { VersionText = "Mihomo Meta v1.19.11 windows amd64 with go1.24.4" };
        FakeMasterWindowsProxy proxy = new() { CurrentState = new WindowsProxyState(true, "127.0.0.1:7890") };
        FakeMasterSettings settings = new() { TransparentProxyEnabled = true };
        MasterControlViewModel viewModel = CreateViewModel(core, proxy, settings);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Core ready: v1.19.11", viewModel.CoreStatusText);
        Assert.Equal("On", viewModel.SystemProxyStatusText);
        Assert.Equal("Standby", viewModel.TransparentProxyStatusText);
    }

    /// <summary>Verifies applying a mode persists the mode, updates statuses, and logs the result.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTakeoverSucceeds_UpdatesStateAndLogs()
    {
        FakeMasterSettings settings = new() { CurrentMode = ClashSharpMode.Disabled };
        FakeMasterTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(ClashSharpMode.FullTakeover, true, true, false, "applied"),
        };
        FakeMasterLog log = new();
        MasterControlViewModel viewModel = CreateViewModel(takeover: takeover, settings: settings, log: log);

        await viewModel.ApplyModeAsync(ClashSharpMode.FullTakeover, CancellationToken.None);

        Assert.Equal(ClashSharpMode.FullTakeover, settings.CurrentMode);
        Assert.True(viewModel.IsFullTakeoverModeSelected);
        Assert.Equal("Running", viewModel.CoreStatusText);
        Assert.Equal("On", viewModel.SystemProxyStatusText);
        Assert.Equal("Off", viewModel.TransparentProxyStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Message == "applied");
    }

    /// <summary>Verifies expected takeover failures move the view model to a faulted state and log an error.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTakeoverFails_SetsFaultedStateAndLogs()
    {
        FakeMasterTakeover takeover = new()
        {
            ExceptionToThrow = new InvalidOperationException("missing core"),
        };
        FakeMasterLog log = new();
        MasterControlViewModel viewModel = CreateViewModel(takeover: takeover, log: log);

        await viewModel.ApplyModeAsync(ClashSharpMode.Standby, CancellationToken.None);

        Assert.Equal(ClashSharpMode.Faulted, viewModel.SelectedMode);
        Assert.Equal("Core failed", viewModel.CoreStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Error" && entry.Detail == "missing core");
    }

    /// <summary>Creates a master control view model with fake dependencies.</summary>
    /// <param name="core">Optional fake core service.</param>
    /// <param name="proxy">Optional fake Windows proxy service.</param>
    /// <param name="settings">Optional fake settings store.</param>
    /// <param name="takeover">Optional fake takeover service.</param>
    /// <param name="log">Optional fake log service.</param>
    /// <returns>A configured master control view model.</returns>
    private static MasterControlViewModel CreateViewModel(
        FakeMasterCore? core = null,
        FakeMasterWindowsProxy? proxy = null,
        FakeMasterSettings? settings = null,
        FakeMasterTakeover? takeover = null,
        FakeMasterLog? log = null)
    {
        return new MasterControlViewModel(
            new FakeMasterLocalization(),
            core ?? new FakeMasterCore(),
            proxy ?? new FakeMasterWindowsProxy(),
            settings ?? new FakeMasterSettings(),
            takeover ?? new FakeMasterTakeover(),
            log ?? new FakeMasterLog());
    }

    /// <summary>Fake localization provider for master-control tests.</summary>
    private sealed class FakeMasterLocalization : IMasterControlLocalization
    {
        /// <summary>Gets a localized string for a key.</summary>
        /// <param name="key">Localization key. Must not be null.</param>
        /// <returns>Localized test string.</returns>
        public string GetString(string key)
        {
            return key switch
            {
                "Nav.MasterControl" => "Master",
                "Page.MasterControl.Description" => "Description",
                "Master.StatusControl.Title" => "Status title",
                "Master.StatusControl.Description" => "Status description",
                "Master.Mode.Disabled.Title" => "Disabled",
                "Master.Mode.Disabled.Description" => "Disabled description",
                "Master.Mode.Standby.Title" => "Standby mode",
                "Master.Mode.Standby.Description" => "Standby description",
                "Master.Mode.RuleTakeover.Title" => "Rule mode",
                "Master.Mode.RuleTakeover.Description" => "Rule description",
                "Master.Mode.FullTakeover.Title" => "Full mode",
                "Master.Mode.FullTakeover.Description" => "Full description",
                "Master.Status.Core" => "Core",
                "Master.Status.SystemProxy" => "System proxy",
                "Master.Status.TransparentProxy" => "Transparent proxy",
                "Master.Status.CoreReady.Format" => "Core ready: {0}",
                "Master.Status.CoreUnavailable" => "Core unavailable",
                "Master.Status.Running" => "Running",
                "Master.Status.NotRunning" => "Not running",
                "Master.Status.On" => "On",
                "Master.Status.Off" => "Off",
                "Master.Status.Fallback" => "Fallback",
                "Master.Status.CoreStartFailed" => "Core failed",
                "Master.Status.Unavailable" => "Unavailable",
                "Master.Status.Standby" => "Standby",
                _ => key,
            };
        }
    }

    /// <summary>Fake core service for master-control tests.</summary>
    private sealed class FakeMasterCore : IMasterControlCore
    {
        /// <summary>Gets or sets the version text returned by the fake core.</summary>
        /// <value>Version text returned by <see cref="GetVersionTextAsync"/>.</value>
        public string VersionText { get; set; } = "mihomo";

        /// <summary>Gets or sets an exception to throw during version probing.</summary>
        /// <value>Exception thrown when non-null.</value>
        public Exception? ExceptionToThrow { get; set; }

        /// <summary>Gets fake core version text.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured version text.</returns>
        public Task<string> GetVersionTextAsync(CancellationToken cancellationToken)
        {
            return ExceptionToThrow is null
                ? Task.FromResult(VersionText)
                : Task.FromException<string>(ExceptionToThrow);
        }
    }

    /// <summary>Fake Windows proxy service for master-control tests.</summary>
    private sealed class FakeMasterWindowsProxy : IMasterControlWindowsProxy
    {
        /// <summary>Gets or sets the proxy state returned by the fake service.</summary>
        /// <value>Current fake proxy state.</value>
        public WindowsProxyState CurrentState { get; set; } = new(false, string.Empty);

        /// <summary>Gets current fake proxy state.</summary>
        /// <returns>Configured fake proxy state.</returns>
        public WindowsProxyState GetCurrentState()
        {
            return CurrentState;
        }
    }

    /// <summary>Fake settings store for master-control tests.</summary>
    private sealed class FakeMasterSettings : IMasterControlSettings
    {
        /// <summary>Gets or sets the current master mode.</summary>
        /// <value>Current fake mode.</value>
        public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;

        /// <summary>Gets or sets whether transparent proxy is enabled.</summary>
        /// <value>True when transparent proxy is enabled.</value>
        public bool TransparentProxyEnabled { get; set; }
    }

    /// <summary>Fake takeover service for master-control tests.</summary>
    private sealed class FakeMasterTakeover : IMasterControlTakeover
    {
        /// <summary>Gets or sets the result returned by the fake takeover service.</summary>
        /// <value>Configured fake takeover result.</value>
        public NetworkTakeoverResult Result { get; set; } = new(ClashSharpMode.Disabled, false, false, false, "disabled");

        /// <summary>Gets or sets an exception to throw when applying a mode.</summary>
        /// <value>Exception thrown when non-null.</value>
        public Exception? ExceptionToThrow { get; set; }

        /// <summary>Applies a fake master mode.</summary>
        /// <param name="mode">Mode requested by the view model.</param>
        /// <returns>Configured takeover result using the requested mode when the result has default disabled mode.</returns>
        public NetworkTakeoverResult ApplyMode(ClashSharpMode mode)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Result.Mode == ClashSharpMode.Disabled && mode != ClashSharpMode.Disabled
                ? Result with { Mode = mode }
                : Result;
        }
    }

    /// <summary>Fake log service for master-control tests.</summary>
    private sealed class FakeMasterLog : IMasterControlLog
    {
        /// <summary>Gets captured log entries.</summary>
        /// <value>Mutable list of captured entries.</value>
        public List<LogEntry> Entries { get; } = [];

        /// <summary>Captures one fake log entry.</summary>
        /// <param name="level">Log level. Must not be null.</param>
        /// <param name="category">Log category. Must not be null.</param>
        /// <param name="message">Log message. Must not be null.</param>
        /// <param name="detail">Optional detail text.</param>
        public void Append(string level, string category, string message, string? detail)
        {
            Entries.Add(new LogEntry(level, category, message, detail));
        }
    }

    /// <summary>Captured log entry.</summary>
    /// <param name="Level">Log level.</param>
    /// <param name="Category">Log category.</param>
    /// <param name="Message">Log message.</param>
    /// <param name="Detail">Optional detail text.</param>
    private sealed record LogEntry(string Level, string Category, string Message, string? Detail);
}

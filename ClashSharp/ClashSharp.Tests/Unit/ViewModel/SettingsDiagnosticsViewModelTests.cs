/*
 * Settings Diagnostics ViewModel Tests
 * Verifies Windows-native diagnostic command routing without WinUI controls
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/SettingsDiagnosticsViewModelTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for settings diagnostics command routing and error handling.</summary>
public sealed class SettingsDiagnosticsViewModelTests
{
    /// <summary>Verifies a diagnose command calls the diagnostic client and logs the result.</summary>
    [Fact]
    public async Task ExecuteCommandAsync_DiagnoseCommand_ReturnsStatusAndLogsInfo()
    {
        FakeDiagnosticsClient client = new()
        {
            DiagnoseResult = new WindowsDiagnosticResult(WindowsDiagnosticTarget.Wsl, "WSL", true, "ready", "detail"),
        };
        FakeDiagnosticsLog log = new();
        SettingsDiagnosticsViewModel viewModel = new(client, log);

        SettingsDiagnosticStatus? status = await viewModel.ExecuteCommandAsync("Wsl:Diagnose", CancellationToken.None);

        Assert.Equal(WindowsDiagnosticTarget.Wsl, status?.Target);
        Assert.Equal("ready", status?.Message);
        Assert.Equal(1, client.DiagnoseCount);
        Assert.Equal(WindowsDiagnosticTarget.Wsl, client.LastTarget);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Message == "ready" && entry.Detail == "detail");
    }

    /// <summary>Verifies apply and reset commands are routed to their dedicated client operations.</summary>
    [Theory]
    [InlineData("Terminal:Apply", "apply", 1, 0)]
    [InlineData("MicrosoftStore:Reset", "reset", 0, 1)]
    public async Task ExecuteCommandAsync_ApplyOrResetCommand_RoutesToExpectedOperation(
        string commandTag,
        string expectedMessage,
        int expectedApplyCount,
        int expectedResetCount)
    {
        FakeDiagnosticsClient client = new()
        {
            ApplyResult = new WindowsDiagnosticResult(WindowsDiagnosticTarget.Terminal, "Terminal", true, "apply", "apply-detail"),
            ResetResult = new WindowsDiagnosticResult(WindowsDiagnosticTarget.MicrosoftStore, "Microsoft Store", true, "reset", "reset-detail"),
        };
        SettingsDiagnosticsViewModel viewModel = new(client, new FakeDiagnosticsLog());

        SettingsDiagnosticStatus? status = await viewModel.ExecuteCommandAsync(commandTag, CancellationToken.None);

        Assert.Equal(expectedMessage, status?.Message);
        Assert.Equal(expectedApplyCount, client.ApplyCount);
        Assert.Equal(expectedResetCount, client.ResetCount);
    }

    /// <summary>Verifies malformed diagnostic commands are ignored and logged as warnings.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Wsl")]
    [InlineData("Unknown:Diagnose")]
    [InlineData("Wsl:Unsupported")]
    public async Task ExecuteCommandAsync_InvalidCommand_ReturnsNullAndLogsWarning(string? commandTag)
    {
        FakeDiagnosticsLog log = new();
        SettingsDiagnosticsViewModel viewModel = new(new FakeDiagnosticsClient(), log);

        SettingsDiagnosticStatus? status = await viewModel.ExecuteCommandAsync(commandTag, CancellationToken.None);

        Assert.Null(status);
        Assert.Contains(log.Entries, entry => entry.Level == "Warning" && entry.Message == "Unsupported diagnostic command.");
    }

    /// <summary>Verifies supported diagnostic exceptions are converted into user-facing status messages.</summary>
    [Theory]
    [InlineData("Wsl:Diagnose", "诊断失败")]
    [InlineData("Wsl:Apply", "应用失败")]
    [InlineData("Wsl:Reset", "还原失败")]
    public async Task ExecuteCommandAsync_DiagnosticFailure_ReturnsFailureStatusAndLogsWarning(string commandTag, string expectedMessage)
    {
        FakeDiagnosticsClient client = new()
        {
            ExceptionToThrow = new InvalidOperationException("boom"),
        };
        FakeDiagnosticsLog log = new();
        SettingsDiagnosticsViewModel viewModel = new(client, log);

        SettingsDiagnosticStatus? status = await viewModel.ExecuteCommandAsync(commandTag, CancellationToken.None);

        Assert.Equal(WindowsDiagnosticTarget.Wsl, status?.Target);
        Assert.Equal(expectedMessage, status?.Message);
        Assert.Contains(log.Entries, entry => entry.Level == "Warning" && entry.Message == expectedMessage && entry.Detail == "boom");
    }

    private sealed class FakeDiagnosticsClient : IWindowsDiagnosticsClient
    {
        public WindowsDiagnosticResult DiagnoseResult { get; set; } = new(WindowsDiagnosticTarget.Wsl, "WSL", true, "diagnose", "diagnose-detail");

        public WindowsDiagnosticResult ApplyResult { get; set; } = new(WindowsDiagnosticTarget.Wsl, "WSL", true, "apply", "apply-detail");

        public WindowsDiagnosticResult ResetResult { get; set; } = new(WindowsDiagnosticTarget.Wsl, "WSL", true, "reset", "reset-detail");

        public Exception? ExceptionToThrow { get; set; }

        public int DiagnoseCount { get; private set; }

        public int ApplyCount { get; private set; }

        public int ResetCount { get; private set; }

        public WindowsDiagnosticTarget LastTarget { get; private set; }

        public Task<WindowsDiagnosticResult> DiagnoseAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
        {
            DiagnoseCount++;
            LastTarget = target;
            return Complete(DiagnoseResult);
        }

        public Task<WindowsDiagnosticResult> ApplyAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
        {
            ApplyCount++;
            LastTarget = target;
            return Complete(ApplyResult);
        }

        public Task<WindowsDiagnosticResult> ResetAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
        {
            ResetCount++;
            LastTarget = target;
            return Complete(ResetResult);
        }

        private Task<WindowsDiagnosticResult> Complete(WindowsDiagnosticResult result)
        {
            return ExceptionToThrow is null
                ? Task.FromResult(result)
                : Task.FromException<WindowsDiagnosticResult>(ExceptionToThrow);
        }
    }

    private sealed class FakeDiagnosticsLog : IDiagnosticsLog
    {
        public List<LogEntry> Entries { get; } = [];

        public void Append(string level, string category, string message, string? detail)
        {
            Entries.Add(new LogEntry(level, category, message, detail));
        }
    }

    private sealed record LogEntry(string Level, string Category, string Message, string? Detail);
}

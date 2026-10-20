using System.Globalization;
using System.Net.Http;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Fact]
    public void DisplayProvenance_RelocalizesNestedResourceArguments()
    {
        string language = "first";
        LocalizedDisplayText inner = new("reason");
        LocalizedDisplayText outer = new("failure", inner, 17);
        string GetText(string key) => key == "failure" ? language + " {0} ({1})" : language + " reason";
        Assert.Equal("first first reason (17)", outer.Resolve(GetText));

        language = "second";

        Assert.Equal("second second reason (17)", outer.Resolve(GetText));
        Assert.Equal(new LocalizedDisplayText("failure", new LocalizedDisplayText("reason"), 17), outer);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("failure")]
    public async Task ConnectionReport_RelocalizesFrozenResultsWithoutRepeatingProbesOrLogs(string failure)
    {
        string language = "first";
        int probes = 0;
        int logs = 0;
        string GetText(string key) => key switch
        {
            "Settings.ConnectionTest.StatusHttp.Format" => language + " HTTP {0}",
            "Settings.ConnectionTest.Failed.Format" => language + " failed {0}",
            "Settings.ConnectionTest.PartialPassed.Format" => language + " {0}/{1}",
            _ => language + ":" + key,
        };
        SettingsViewModel viewModel = CreateConnectionTestViewModel((uri, _) =>
        {
            probes++;
            if (!uri.Host.Contains("google", StringComparison.Ordinal)) { return Task.FromResult(204); }
            return failure switch
            {
                "http" => Task.FromResult(503),
                "timeout" => Task.FromException<int>(new TaskCanceledException()),
                _ => Task.FromException<int>(new HttpRequestException("provider failure")),
            };
        }, (_, _, _, _) => logs++, GetText);
        ConnectionTestReport report = await viewModel.RunConnectionTestAsync(CancellationToken.None);
        ConnectionTestDisplay display = new(report, GetText);
        ConnectionTestTargetDisplay[] rows = [.. display.Rows];
        int?[] elapsed = report.Results.Select(result => result.ObservedElapsedMilliseconds).ToArray();
        string capturedStatus = report.Results[0].StatusText;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fa-IR");
            language = "second";
            display.RefreshLocalization();
            viewModel.RefreshLocalization();

            Assert.Equal(3, probes);
            Assert.Equal(1, logs);
            Assert.Equal("second 2/3", display.SummaryText);
            Assert.Equal(ConnectionTestSummaryState.PartialFailed, display.SummaryState);
            Assert.Equal(capturedStatus, report.Results[0].StatusText);
            Assert.All(display.Rows, row => Assert.StartsWith("second:", row.Label, StringComparison.Ordinal));
            for (int index = 0; index < rows.Length; index++)
            {
                Assert.Same(rows[index], display.Rows[index]);
                Assert.Same(report.Results[index], display.Rows[index].Model);
                Assert.Equal(report.Results[index].Url, display.Rows[index].Url);
                Assert.NotNull(elapsed[index]);
                Assert.Equal(elapsed[index]!.Value.ToString(CultureInfo.CurrentCulture) + " ms", display.Rows[index].LatencyText);
            }
            Assert.Equal(failure switch
            {
                "http" => "second HTTP 503",
                "timeout" => "second:Settings.ConnectionTest.TimedOut",
                _ => "second failed second:Application.UnexpectedError",
            }, display.Rows[0].StatusText);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void ConnectionReport_CopiesRowsAndPreservesUntaggedProviderPresentation()
    {
        ConnectionTestTargetResult provider = new("provider label", "https://example.invalid/frozen", false, "HTTP provider text", "provider elapsed", null);
        List<ConnectionTestTargetResult> input = [provider];
        ConnectionTestDisplay display = new(new(input, "provider summary", ConnectionTestSummaryState.AllFailed), _ => "translation");
        input.Clear();

        display.RefreshLocalization();

        ConnectionTestTargetDisplay row = Assert.Single(display.Rows);
        Assert.Same(provider, row.Model);
        Assert.Equal("provider label", row.Label);
        Assert.Equal("HTTP provider text", row.StatusText);
        Assert.Equal("provider elapsed", row.LatencyText);
        Assert.Equal("provider summary", display.SummaryText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiagnosticStatus_LanguageChangesRetainPendingCommandAndObservedProviderFacts(bool tagged)
    {
        string language = "first";
        string GetText(string key) => language + ":" + key;
        RetainedDiagnosticClient client = new();
        RetainedDiagnosticLog log = new();
        SettingsDiagnosticsViewModel diagnostics = new(client, log, GetText);
        SettingsViewModel viewModel = CreateConnectionTestViewModel((_, _) => Task.FromResult(204), getString: GetText, diagnosticsViewModel: diagnostics);
        Task execution = viewModel.ExecuteWindowsDiagnosticCommandAsync("Terminal:Diagnose", CancellationToken.None);
        language = "second";

        viewModel.RefreshLocalization();
        viewModel.RefreshDiagnosticStatusPresentation();

        Assert.False(execution.IsCompleted);
        Assert.Equal("second:Diagnostic.NotRun", viewModel.TerminalDiagnosticStatusText);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, log.Calls);
        client.Completion.SetResult(new(WindowsDiagnosticTarget.Terminal, "Terminal", true, "captured provider message", "captured detail")
        { MessageResourceKey = tagged ? "WindowsDiagnostic.Terminal.Ready" : null });
        await execution;
        language = "third";
        viewModel.RefreshLocalization();
        viewModel.RefreshDiagnosticStatusPresentation();

        Assert.Equal(tagged ? "third:WindowsDiagnostic.Terminal.Ready" : "captured provider message", viewModel.TerminalDiagnosticStatusText);
        Assert.Equal("third:Diagnostic.NotRun", viewModel.WslDiagnosticStatusText);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, log.Calls);
        Assert.Equal("captured provider message", log.Message);
        Assert.Equal("captured detail", log.Detail);
    }

    private sealed class RetainedDiagnosticClient : IWindowsDiagnosticsClient
    {
        public TaskCompletionSource<WindowsDiagnosticResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<WindowsDiagnosticResult> DiagnoseAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
        { Calls++; return Completion.Task; }
        public Task<WindowsDiagnosticResult> ApplyAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("No apply is authorized by this display refresh."); }
        public Task<WindowsDiagnosticResult> ResetAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("No reset is authorized by this display refresh."); }
    }

    private sealed class RetainedDiagnosticLog : IDiagnosticsLog
    {
        public int Calls { get; private set; }
        public string? Message { get; private set; }
        public string? Detail { get; private set; }
        public void Append(string level, string category, string message, string? detail)
        { Calls++; Message = message; Detail = detail; }
    }
}

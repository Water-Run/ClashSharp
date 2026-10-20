using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed class McpSettingsViewModelTests
{
    [Fact]
    public async Task LocalizationRefresh_WhileObservationIsPending_PreservesDraftAndDoesNotReadAgain()
    {
        string language = "zh";
        Management service = new();
        McpSettingsViewModel viewModel = new(service, key => language + ":" + key, new Errors());
        await viewModel.RefreshAsync(CancellationToken.None);
        viewModel.Enabled = true;
        viewModel.Port = 17901;
        McpScopeChoice choice = viewModel.Scopes.Single(scope => scope.Scope == McpAccessScope.ChangeProxyState);
        choice.IsGranted = true;
        service.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task observation = viewModel.RefreshObservedAsync(CancellationToken.None);
        int reads = service.Reads;
        string? title = null;
        viewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(viewModel.TitleText)) { title = viewModel.TitleText; } };

        language = "de";
        viewModel.RefreshLocalization();

        Assert.False(observation.IsCompleted);
        Assert.Equal("de:Settings.Mcp.Title", title);
        Assert.StartsWith("de:", choice.Title, StringComparison.Ordinal);
        Assert.True(viewModel.Enabled);
        Assert.Equal(17901, viewModel.Port);
        Assert.Same(choice, viewModel.Scopes.Single(scope => scope.Scope == McpAccessScope.ChangeProxyState));
        Assert.True(choice.IsGranted);
        Assert.Equal(reads, service.Reads);
        Assert.Equal(0, service.Applies);
        Assert.Equal(0, service.Copies);
        service.PendingRead.SetResult(service.Snapshot);
        await observation;
        Assert.True(choice.IsGranted);
        Assert.Equal(17901, viewModel.Port);
    }

    [Fact]
    public async Task DraftChangesDoNotGrantAccessUntilExplicitApply()
    {
        Management service = new();
        McpSettingsViewModel viewModel = new(service, key => key, new Errors());
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.Equal(8, viewModel.Scopes.Count);
        Assert.Single(viewModel.Scopes, scope => scope.IsGranted);
        viewModel.Enabled = true;
        viewModel.Scopes.Single(scope => scope.Scope == McpAccessScope.ChangeProxyState).IsGranted = true;
        await viewModel.RefreshObservedAsync(CancellationToken.None);
        Assert.True(viewModel.Enabled);
        Assert.True(viewModel.Scopes.Single(scope => scope.Scope == McpAccessScope.ChangeProxyState).IsGranted);
        Assert.Equal(0, service.Applies);
        await viewModel.ApplyAsync(CancellationToken.None);
        Assert.Equal(1, service.Applies);
        Assert.True(service.Snapshot.Desired.Access.Allows(McpAccessScope.ChangeProxyState));
        Assert.False(viewModel.CanCopy); // A requested enable without an observed listener is not running.
        Assert.EndsWith("Unavailable", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(17892.5)]
    public async Task InvalidPortDoesNotReachRuntime(double port)
    {
        Management service = new();
        McpSettingsViewModel viewModel = new(service, key => key, new Errors()) { Port = port };
        await viewModel.ApplyAsync(CancellationToken.None);
        Assert.Equal(0, service.Applies);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task CopyRequiresExplicitActionAndDoesNotRetainTokenInStatus()
    {
        Management service = new();
        McpSettingsViewModel viewModel = new(service, key => key, new Errors());
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, service.Copies);
        string? copied = null;
        await viewModel.CopyAsync(text => { copied = text; return true; }, CancellationToken.None);
        Assert.Equal("private-fixture", copied);
        Assert.Equal(1, service.Copies);
        Assert.False(viewModel.HasError);
        Assert.Equal("Settings.Mcp.Copied", viewModel.Message);
        Assert.DoesNotContain("private-fixture", viewModel.Message + viewModel.Status + viewModel.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedClipboardWritePreservesUnappliedDraftAndAllowsRetry()
    {
        McpServerConfiguration running = new(17892, new(true, McpAccessScope.ReadStatus));
        Management service = new() { Snapshot = new(running, new(running, new("http://127.0.0.1:17892/mcp"), true, false, null)) };
        Errors errors = new();
        McpSettingsViewModel viewModel = new(service, key => key, errors);
        await viewModel.RefreshAsync(CancellationToken.None);
        viewModel.Enabled = false;
        viewModel.Port = 17901;
        viewModel.Scopes.Single(scope => scope.Scope == McpAccessScope.ChangeProxyState).IsGranted = true;

        await viewModel.CopyAsync(_ => false, CancellationToken.None);

        Assert.True(viewModel.HasError);
        Assert.True(viewModel.HasMessage);
        Assert.Equal(PageStatusSeverity.Error, viewModel.MessageSeverity);
        Assert.Equal("Settings.Mcp.CopyFailed", viewModel.Message);
        Assert.True(viewModel.IsInputEnabled);
        Assert.False(viewModel.Enabled);
        Assert.True(viewModel.CanCopy);
        Assert.Equal(17901, viewModel.Port);
        Assert.True(viewModel.Scopes.Single(scope => scope.Scope == McpAccessScope.ChangeProxyState).IsGranted);
        Assert.Equal(0, service.Applies);
        Assert.Empty(errors.Values);

        await viewModel.CopyAsync(_ => true, CancellationToken.None);

        Assert.False(viewModel.HasError);
        Assert.True(viewModel.HasMessage);
        Assert.Equal(PageStatusSeverity.Success, viewModel.MessageSeverity);
        Assert.Equal("Settings.Mcp.Copied", viewModel.Message);
        Assert.False(viewModel.Enabled);
        Assert.Equal(17901, viewModel.Port);
        Assert.Equal(2, service.Copies);
        Assert.True(service.Snapshot.Desired.Access.Enabled);
    }

    [Fact]
    public async Task CancellationAfterConfigurationReadDoesNotWriteClipboard()
    {
        using CancellationTokenSource cancellation = new();
        Management service = new() { CancelClientRead = cancellation };
        Errors errors = new();
        McpSettingsViewModel viewModel = new(service, key => key, errors);
        bool clipboardInvoked = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewModel.CopyAsync(_ =>
        {
            clipboardInvoked = true;
            return true;
        }, cancellation.Token));

        Assert.False(clipboardInvoked);
        Assert.True(viewModel.IsInputEnabled);
        Assert.Empty(viewModel.Message);
        Assert.Empty(errors.Values);
    }

    [Fact]
    public async Task BackgroundObservationPreservesFeedbackAndUnappliedDraft()
    {
        Management service = new();
        string language = "en";
        McpSettingsViewModel viewModel = new(service, key => language + ":" + key, new Errors());
        await viewModel.CopyAsync(_ => false, CancellationToken.None);
        viewModel.Enabled = true;
        viewModel.Port = 17901;
        List<string?> changes = [];
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        language = "fr";

        await viewModel.RefreshObservedAsync(CancellationToken.None);

        Assert.Equal("fr:Settings.Mcp.CopyFailed", viewModel.Message);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.HasMessage);
        Assert.Equal(PageStatusSeverity.Error, viewModel.MessageSeverity);
        Assert.Contains(nameof(McpSettingsViewModel.Message), changes);
        Assert.True(viewModel.Enabled);
        Assert.Equal(17901, viewModel.Port);
        Assert.Equal(0, service.Applies);
        Assert.Equal(1, service.Copies);
    }

    [Fact]
    public async Task DismissedFeedbackStaysClosedDuringObservationAndReopensOnRetry()
    {
        Management service = new();
        McpSettingsViewModel viewModel = new(service, key => key, new Errors());
        await viewModel.CopyAsync(_ => true, CancellationToken.None);
        viewModel.Enabled = true;
        viewModel.Port = 17901;

        viewModel.IsMessageOpen = false;
        await viewModel.RefreshObservedAsync(CancellationToken.None);

        Assert.False(viewModel.HasMessage);
        Assert.False(viewModel.IsMessageOpen);
        Assert.False(viewModel.HasError);
        Assert.Empty(viewModel.Message);
        Assert.Equal(PageStatusSeverity.Informational, viewModel.MessageSeverity);
        Assert.True(viewModel.Enabled);
        Assert.Equal(17901, viewModel.Port);
        Assert.Equal(0, service.Applies);

        await viewModel.CopyAsync(_ => false, CancellationToken.None);

        Assert.True(viewModel.HasMessage);
        Assert.True(viewModel.IsMessageOpen);
        Assert.True(viewModel.HasError);
        Assert.Equal(PageStatusSeverity.Error, viewModel.MessageSeverity);
        Assert.Equal("Settings.Mcp.CopyFailed", viewModel.Message);
        Assert.Equal(0, service.Applies);
    }

    [Fact]
    public void EmptyFeedbackCannotBeOpenedByTheView()
    {
        Management service = new();
        McpSettingsViewModel viewModel = new(service, key => key, new Errors());

        viewModel.IsMessageOpen = true;
        viewModel.IsMessageOpen = false;

        Assert.False(viewModel.IsMessageOpen);
        Assert.False(viewModel.HasMessage);
        Assert.Empty(viewModel.Message);
        Assert.Equal(0, service.Applies);
        Assert.Equal(0, service.Copies);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BackgroundFailurePreservesDraftAndReplacesEarlierSuccessWithSanitizedError(int failures)
    {
        Management service = new();
        Errors errors = new();
        McpSettingsViewModel viewModel = new(service, key => key, errors);
        await viewModel.CopyAsync(_ => true, CancellationToken.None);
        viewModel.Enabled = true;
        viewModel.Port = 17901;
        service.ReadFailuresRemaining = failures;

        await viewModel.RefreshObservedAsync(CancellationToken.None);

        Assert.True(viewModel.HasMessage);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.CanCopy);
        Assert.True(viewModel.Enabled);
        Assert.Equal(17901, viewModel.Port);
        Assert.Equal(failures == 1 ? "Settings.Mcp.Disabled" : "Settings.Mcp.Unknown", viewModel.Status);
        Assert.Equal(0, service.Applies);
        Assert.Equal(PageStatusSeverity.Error, viewModel.MessageSeverity);
        Assert.Equal("Settings.Mcp.Failed", viewModel.Message);
        Assert.DoesNotContain("private-fixture", viewModel.Message + Assert.Single(errors.Values).Exception, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitStatusRefreshPreservesDraftAndHidesPreviousFeedbackWhilePending()
    {
        Management service = new();
        McpSettingsViewModel viewModel = new(service, key => key, new Errors());
        await viewModel.CopyAsync(_ => false, CancellationToken.None);
        viewModel.Enabled = true;
        viewModel.Port = 17901;
        TaskCompletionSource<McpSettingsSnapshot> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PendingRead = pending;

        Task refresh = viewModel.RefreshStatusAsync(CancellationToken.None);

        Assert.False(viewModel.IsInputEnabled);
        Assert.False(viewModel.HasMessage);
        Assert.False(viewModel.HasError);
        Assert.Equal(PageStatusSeverity.Informational, viewModel.MessageSeverity);

        pending.SetResult(service.Snapshot);
        await refresh;

        Assert.True(viewModel.IsInputEnabled);
        Assert.False(viewModel.HasMessage);
        Assert.Empty(viewModel.Message);
        Assert.True(viewModel.Enabled);
        Assert.Equal(17901, viewModel.Port);
        Assert.Equal(0, service.Applies);
    }

    [Fact]
    public async Task FailedConfigurationReadPreservesDraftWithoutInvokingClipboard()
    {
        Management service = new() { FailClientRead = true };
        Errors errors = new();
        McpSettingsViewModel viewModel = new(service, key => key, errors) { Enabled = true, Port = 17901 };
        bool copied = false;

        await viewModel.CopyAsync(_ => { copied = true; return true; }, CancellationToken.None);

        Assert.False(copied);
        Assert.True(viewModel.Enabled);
        Assert.Equal(17901, viewModel.Port);
        Assert.Equal(0, service.Applies);
        Assert.True(viewModel.HasMessage);
        Assert.Equal(PageStatusSeverity.Error, viewModel.MessageSeverity);
        Assert.Equal("Settings.Mcp.Failed", viewModel.Message);
        Assert.DoesNotContain("private-fixture", viewModel.Message + Assert.Single(errors.Values).Exception, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureRefreshesActualStateWithoutExposingExceptionContent()
    {
        Management service = new() { FailApply = true };
        Errors errors = new();
        McpSettingsViewModel viewModel = new(service, key => key, errors) { Enabled = true };
        await viewModel.ApplyAsync(CancellationToken.None);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.Enabled);
        Assert.DoesNotContain("private-fixture", viewModel.Message + Assert.Single(errors.Values).Exception, StringComparison.Ordinal);
    }

    private sealed class Management : IMcpServerManagement
    {
        public int Reads { get; private set; }
        public int Applies { get; private set; }
        public int Copies { get; private set; }
        public bool FailApply { get; set; }
        public int ReadFailuresRemaining { get; set; }
        public bool FailClientRead { get; init; }
        public TaskCompletionSource<McpSettingsSnapshot>? PendingRead { get; set; }
        public CancellationTokenSource? CancelClientRead { get; init; }
        public McpSettingsSnapshot Snapshot { get; set; } = new(new(17892, McpAccessPolicy.Default), new(new(17892, McpAccessPolicy.Default), null, false, false, null));
        public Task<McpSettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (ReadFailuresRemaining > 0) { ReadFailuresRemaining--; throw new IOException("private-fixture"); }
            return PendingRead?.Task ?? Task.FromResult(Snapshot);
        }
        public Task<McpSettingsSnapshot> ApplyAsync(McpServerConfiguration configuration, CancellationToken cancellationToken)
        {
            Applies++;
            if (FailApply) { throw new IOException("private-fixture"); }
            Snapshot = new(configuration, new(configuration, null, false, false, null));
            return Task.FromResult(Snapshot);
        }
        public Task<string> GetClientConfigurationAsync(CancellationToken cancellationToken)
        {
            Copies++;
            if (FailClientRead) { throw new IOException("private-fixture"); }
            CancelClientRead?.Cancel();
            return Task.FromResult("private-fixture");
        }
        public Task<McpSettingsSnapshot> RotateCredentialAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }

    private sealed class Errors : IApplicationErrorSink
    {
        public List<ApplicationError> Values { get; } = [];
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) { Values.Add(error); return Task.CompletedTask; }
    }
}

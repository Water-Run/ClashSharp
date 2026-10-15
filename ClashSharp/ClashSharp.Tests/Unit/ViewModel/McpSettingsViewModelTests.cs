using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed class McpSettingsViewModelTests
{
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
        await viewModel.CopyAsync(text => copied = text, CancellationToken.None);
        Assert.Equal("private-fixture", copied);
        Assert.Equal(1, service.Copies);
        Assert.DoesNotContain("private-fixture", viewModel.Message + viewModel.Status + viewModel.Endpoint, StringComparison.Ordinal);
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
        public int Applies { get; private set; }
        public int Copies { get; private set; }
        public bool FailApply { get; set; }
        public McpSettingsSnapshot Snapshot { get; private set; } = new(new(17892, McpAccessPolicy.Default), new(new(17892, McpAccessPolicy.Default), null, false, false, null));
        public Task<McpSettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<McpSettingsSnapshot> ApplyAsync(McpServerConfiguration configuration, CancellationToken cancellationToken)
        {
            Applies++;
            if (FailApply) { throw new IOException("private-fixture"); }
            Snapshot = new(configuration, new(configuration, null, false, false, null));
            return Task.FromResult(Snapshot);
        }
        public Task<string> GetClientConfigurationAsync(CancellationToken cancellationToken) { Copies++; return Task.FromResult("private-fixture"); }
        public Task<McpSettingsSnapshot> RotateCredentialAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }

    private sealed class Errors : IApplicationErrorSink
    {
        public List<ApplicationError> Values { get; } = [];
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) { Values.Add(error); return Task.CompletedTask; }
    }
}

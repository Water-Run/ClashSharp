using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.ViewModel;

/// <summary>Owns an explicit permission draft and displays independently observed endpoint state.</summary>
internal sealed class McpSettingsViewModel(IMcpServerManagement management, Func<string, string> getString, IApplicationErrorSink errors) : ObservableObject
{
    private bool _enabled;
    private double _port = McpServerConfiguration.DefaultPort;
    private bool _busy;
    private string _messageKey = string.Empty;
    private bool _hasError;
    private McpRuntimeSnapshot? _runtime;

    public IReadOnlyList<McpScopeChoice> Scopes { get; } = Enum.GetValues<McpAccessScope>()
        .Where(scope => scope != McpAccessScope.None).Select(scope => new McpScopeChoice(scope, getString)).ToArray();
    private string Text(string key) => getString("Settings.Mcp." + key);
    public string TitleText => Text("Title");
    public string DescriptionText => Text("Description");
    public string EnabledText => Text("Enabled");
    public string EnabledDescriptionText => Text("EnabledDescription");
    public string PortText => Text("Port");
    public string PortDescriptionText => Text("PortDescription");
    public string StatusText => Text("Status");
    public string RefreshText => Text("Refresh");
    public string PermissionsText => Text("Permissions");
    public string PermissionsDescriptionText => Text("PermissionsDescription");
    public string ApplyText => Text("Apply");
    public string ClientText => Text("Client");
    public string ClientDescriptionText => Text("ClientDescription");
    public string CopyText => Text("Copy");
    public string CredentialText => Text("Credential");
    public string CredentialDescriptionText => Text("CredentialDescription");
    public string RotateText => Text("Rotate");
    public string ResetText => Text("Reset");
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public double Port { get => _port; set => SetProperty(ref _port, value); }
    public bool IsInputEnabled => !_busy;
    public bool CanCopy => !_busy && _runtime is { BoundEndpoint: not null, HasCredential: true, IsSuspended: false };
    public bool HasError => _hasError;
    public bool HasMessage => _messageKey.Length > 0;
    /// <summary>Synchronizes the native close state without allowing an empty result to open.</summary>
    public bool IsMessageOpen
    {
        get => HasMessage;
        set
        {
            if (!value && HasMessage) { DismissMessage(); }
        }
    }
    public string Message => HasMessage ? Text(_messageKey) : string.Empty;
    public PageStatusSeverity MessageSeverity => !HasMessage ? PageStatusSeverity.Informational
        : _hasError ? PageStatusSeverity.Error : PageStatusSeverity.Success;
    public string Status => Text(_runtime is null ? "Unknown" : _runtime.IsSuspended ? "Paused"
        : _runtime.BoundEndpoint is not null ? "Running" : _runtime.ErrorCode is not null || _runtime.Configuration.Access.Enabled ? "Unavailable" : "Disabled");
    public string Endpoint => _runtime?.BoundEndpoint?.AbsoluteUri ?? string.Empty;

    public Task RefreshAsync(CancellationToken cancellationToken) => RunActionAsync(async () =>
        Load(await management.ReadAsync(cancellationToken), loadDraft: true), cancellationToken);

    public Task RefreshObservedAsync(CancellationToken cancellationToken) => RunAsync(async () =>
        Load(await management.ReadAsync(cancellationToken), loadDraft: false), loadDraftOnFailure: false, cancellationToken);

    /// <summary>Refreshes the status row without replacing unsaved settings or grants.</summary>
    public Task RefreshStatusAsync(CancellationToken cancellationToken) => RunActionAsync(async () =>
        Load(await management.ReadAsync(cancellationToken), loadDraft: false), loadDraftOnFailure: false, cancellationToken);

    public Task ApplyAsync(CancellationToken cancellationToken) => RunActionAsync(async () =>
    {
        if (!double.IsFinite(Port) || Port != Math.Truncate(Port) || Port is < 1024 or > 65535)
        {
            SetMessage("InvalidPort", true);
            return;
        }
        McpAccessScope scopes = Scopes.Where(choice => choice.IsGranted).Aggregate(McpAccessScope.None, (combined, choice) => combined | choice.Scope);
        Load(await management.ApplyAsync(new((int)Port, new(Enabled, scopes)), cancellationToken), loadDraft: true);
        SetMessage("Applied", false);
    }, cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken) => RunActionAsync(async () =>
    {
        Load(await management.ApplyAsync(new(McpServerConfiguration.DefaultPort, McpAccessPolicy.Default), cancellationToken), loadDraft: true);
        SetMessage("Applied", false);
    }, cancellationToken);

    public Task RotateAsync(CancellationToken cancellationToken) => RunActionAsync(async () =>
    {
        Load(await management.RotateCredentialAsync(cancellationToken), loadDraft: true);
        SetMessage("Rotated", false);
    }, cancellationToken);

    public Task CopyAsync(Func<string, bool> copyToClipboard, CancellationToken cancellationToken) => RunActionAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(copyToClipboard);
        string configuration = await management.GetClientConfigurationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        bool copied = copyToClipboard(configuration);
        SetMessage(copied ? "Copied" : "CopyFailed", !copied);
    }, loadDraftOnFailure: false, cancellationToken);

    /// <summary>Dismisses feedback without modifying the permission draft or observed runtime.</summary>
    public void DismissMessage() => SetMessage(string.Empty, false);

    // Only explicit user actions replace earlier feedback. Background observations refresh
    // runtime state and localization while leaving the current result available to the user.
    private Task RunActionAsync(Func<Task> operation, CancellationToken cancellationToken) =>
        RunActionAsync(operation, loadDraftOnFailure: true, cancellationToken);

    private Task RunActionAsync(Func<Task> operation, bool loadDraftOnFailure, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        SetMessage(string.Empty, false);
        await operation();
    }, loadDraftOnFailure, cancellationToken);

    private async Task RunAsync(Func<Task> operation, bool loadDraftOnFailure, CancellationToken cancellationToken)
    {
        if (_busy) { return; }
        SetBusy(true);
        try { await operation(); }
        catch (Exception failure) when (ExceptionGraphClassifier.IsRecoverable(failure)
            && !ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken))
        {
            SetMessage("Failed", true);
            await errors.ReportAsync(new("Mcp.Settings", new InvalidOperationException("MCP management failed: " + failure.GetType().Name)), CancellationToken.None);
            try { Load(await management.ReadAsync(cancellationToken), loadDraft: loadDraftOnFailure); }
            catch (Exception refreshFailure) when (ExceptionGraphClassifier.IsRecoverable(refreshFailure)
                && !ExceptionGraphClassifier.IsCallerCancellation(refreshFailure, cancellationToken))
            {
                _runtime = null;
                NotifyRuntime();
            }
        }
        finally { SetBusy(false); }
    }

    private void Load(McpSettingsSnapshot snapshot, bool loadDraft)
    {
        _runtime = snapshot.Runtime;
        if (loadDraft)
        {
            Enabled = snapshot.Desired.Access.Enabled;
            Port = snapshot.Desired.Port;
            foreach (McpScopeChoice choice in Scopes) { choice.IsGranted = (snapshot.Desired.Access.Scopes & choice.Scope) == choice.Scope; }
        }
        RefreshLocalization();
    }

    /// <summary>Updates labels while retaining the permission draft and the last observed runtime.</summary>
    public void RefreshLocalization()
    {
        foreach (string property in new[] { nameof(TitleText), nameof(DescriptionText), nameof(EnabledText), nameof(EnabledDescriptionText),
            nameof(PortText), nameof(PortDescriptionText), nameof(StatusText), nameof(RefreshText), nameof(PermissionsText), nameof(PermissionsDescriptionText),
            nameof(ApplyText), nameof(ClientText), nameof(ClientDescriptionText), nameof(CopyText), nameof(CredentialText), nameof(CredentialDescriptionText),
            nameof(RotateText), nameof(ResetText), nameof(Message) }) { OnPropertyChanged(property); }
        foreach (McpScopeChoice choice in Scopes) { choice.RefreshLabels(); }
        NotifyRuntime();
    }

    private void NotifyRuntime()
    {
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Endpoint)); OnPropertyChanged(nameof(CanCopy));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(IsInputEnabled)); OnPropertyChanged(nameof(CanCopy));
    }

    private void SetMessage(string key, bool hasError)
    {
        _messageKey = key; _hasError = hasError;
        OnPropertyChanged(nameof(Message)); OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(MessageSeverity)); OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(IsMessageOpen));
    }
}

/// <summary>One independently editable agent grant; changing a draft never changes live authorization.</summary>
internal sealed class McpScopeChoice(McpAccessScope scope, Func<string, string> getString) : ObservableObject
{
    private bool _granted;
    public McpAccessScope Scope { get; } = scope;
    public string Title => getString("Settings.Mcp.Scope." + Scope);
    public string Description => getString("Settings.Mcp.Scope." + Scope + ".Description");
    public bool IsGranted { get => _granted; set => SetProperty(ref _granted, value); }
    public void RefreshLabels() { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Description)); }
}

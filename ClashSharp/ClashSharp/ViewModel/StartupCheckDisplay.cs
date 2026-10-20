using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Retains a startup observation while refreshing only its application-authored display text.</summary>
internal sealed class StartupCheckDisplay : ObservableObject
{
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _statusText = string.Empty;
    private string _actionText = string.Empty;

    public StartupCheckDisplay(StartupCheckItem model, Func<string, string> getString, bool allowNavigation)
    {
        Model = model;
        NavigationTag = allowNavigation ? model.Kind switch
        {
            StartupCheckKind.Subscription => "Links",
            StartupCheckKind.TransparentProxy or StartupCheckKind.StartupRecovery or StartupCheckKind.SystemProxy => "Settings",
            _ => null,
        } : null;
        RefreshLocalization(getString);
    }

    public StartupCheckItem Model { get; }
    public string Title => _title;
    public string Description => _description;
    public string StatusText => _statusText;
    public string ActionText => _actionText;
    public string? NavigationTag { get; }
    public bool HasNavigation => NavigationTag is not null;
    public string ActionAutomationName => $"{Title}: {ActionText}";
    public string Glyph => Model.State switch
    {
        StartupCheckState.Healthy => "\uE73E",
        StartupCheckState.Optional => "\uE946",
        _ => "\uE7BA",
    };

    public void RefreshLocalization(Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        SetProperty(ref _title, Model.TitleSource?.Resolve(getString) ?? Model.Title, nameof(Title));
        SetProperty(ref _description, Model.DescriptionSource?.Resolve(getString) ?? Model.Description, nameof(Description));
        SetProperty(ref _statusText, getString($"StartupPrompt.State.{Model.State}"), nameof(StatusText));
        SetProperty(ref _actionText, getString(Model.Kind == StartupCheckKind.Subscription
            ? "StartupPrompt.Action.Subscription" : "StartupPrompt.Action.Settings"), nameof(ActionText));
        OnPropertyChanged(nameof(ActionAutomationName));
    }
}

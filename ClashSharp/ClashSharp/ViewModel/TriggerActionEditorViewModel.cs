using System;
using ClashSharp.Model.Triggers;
using ClashSharpMode = global::ClashSharp.Model.ClashSharpMode;
using TriggerAction = global::ClashSharp.Model.Triggers.TriggerAction;
using TriggerActionKind = global::ClashSharp.Model.Triggers.TriggerActionKind;

namespace ClashSharp.ViewModel;

/// <summary>Owns one ordered editable trigger-action draft and typed parameter validation.</summary>
internal sealed class TriggerActionEditorViewModel : ObservableObject
{
    private readonly Func<string, string> _getString;
    private bool _booleanValue;
    private ClashSharpMode _proxyMode;
    private string _notificationMessage;
    private string? _errorCode;

    /// <summary>Initializes a draft that preserves an existing action's typed parameters.</summary>
    public TriggerActionEditorViewModel(
        TriggerAction action,
        Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(action);
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        Kind = action.Kind;
        _booleanValue = true;
        _proxyMode = ClashSharpMode.RuleTakeover;
        _notificationMessage = _getString("Notification.Custom.Message");
        switch (action.Parameters)
        {
            case NoActionParameters:
                break;
            case BooleanActionParameters parameters:
                _booleanValue = parameters.Value;
                break;
            case ProxyModeActionParameters parameters:
                _proxyMode = parameters.Mode;
                break;
            case NotificationActionParameters parameters:
                _notificationMessage = parameters.Message;
                break;
            default:
                throw new ArgumentException(
                    "The action contains an unsupported parameter shape.",
                    nameof(action));
        }
    }

    /// <summary>Gets the action kind, which remains stable while the row is edited.</summary>
    public TriggerActionKind Kind { get; }

    public string Title => _getString($"Triggers.Action.{Kind}");

    public string Description => _getString($"Triggers.Action.{Kind}.Description");

    /// <summary>Gets the localized accessible label for moving this action earlier.</summary>
    public string MoveUpText => _getString("Command.MoveUp");

    /// <summary>Gets the localized accessible label for moving this action later.</summary>
    public string MoveDownText => _getString("Command.MoveDown");

    /// <summary>Gets the localized accessible label for removing this action.</summary>
    public string RemoveText => _getString("Command.Delete");

    public bool IsBooleanVisible => Kind is TriggerActionKind.SetLaunchAtStartup
        or TriggerActionKind.SetTransparentProxy
        or TriggerActionKind.SetConnectionSampling;

    public bool IsProxyModeVisible => Kind == TriggerActionKind.SwitchProxyMode;

    public bool IsNotificationMessageVisible => Kind == TriggerActionKind.SendNotification;

    public bool BooleanValue
    {
        get => _booleanValue;
        set
        {
            if (SetProperty(ref _booleanValue, value))
            {
                ErrorCode = null;
            }
        }
    }

    public ClashSharpMode ProxyMode
    {
        get => _proxyMode;
        set
        {
            if (SetProperty(ref _proxyMode, value))
            {
                ErrorCode = null;
            }
        }
    }

    public string NotificationMessage
    {
        get => _notificationMessage;
        set
        {
            if (SetProperty(ref _notificationMessage, value ?? string.Empty))
            {
                ErrorCode = null;
            }
        }
    }

    public string? ErrorCode
    {
        get => _errorCode;
        private set
        {
            if (SetProperty(ref _errorCode, value))
            {
                OnPropertyChanged(nameof(ErrorMessage));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string? ErrorMessage => ErrorCode is null
        ? null
        : _getString(ErrorCode == "trigger.action.notification.message.required"
            ? "Triggers.Validation.NotificationMessageRequired"
            : "Triggers.Validation.InvalidAction");

    public bool HasError => ErrorCode is not null;

    /// <summary>Creates a validated typed action from this draft.</summary>
    public bool TryBuild(out TriggerAction? action)
    {
        ErrorCode = null;
        TriggerActionParameters? parameters = Kind switch
        {
            TriggerActionKind.CloseConnections or TriggerActionKind.ExitApplication => new NoActionParameters(),
            TriggerActionKind.SetLaunchAtStartup or
                TriggerActionKind.SetTransparentProxy or
                TriggerActionKind.SetConnectionSampling => new BooleanActionParameters(BooleanValue),
            TriggerActionKind.SwitchProxyMode when Enum.IsDefined(ProxyMode)
                && ProxyMode != ClashSharpMode.Faulted => new ProxyModeActionParameters(ProxyMode),
            TriggerActionKind.SendNotification when !string.IsNullOrWhiteSpace(NotificationMessage) =>
                new NotificationActionParameters(NotificationMessage),
            TriggerActionKind.SwitchProxyMode => Fail("trigger.action.mode.undefined"),
            TriggerActionKind.SendNotification => Fail("trigger.action.notification.message.required"),
            _ => Fail("trigger.action.kind.undefined"),
        };
        action = parameters is null ? null : new TriggerAction(Kind, parameters);
        return action is not null;
    }

    /// <summary>Creates a default typed action draft for one supported kind.</summary>
    public static TriggerActionEditorViewModel Create(
        TriggerActionKind kind,
        Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        TriggerAction action = kind switch
        {
            TriggerActionKind.CloseConnections or TriggerActionKind.ExitApplication =>
                new TriggerAction(kind, new NoActionParameters()),
            TriggerActionKind.SetLaunchAtStartup or
                TriggerActionKind.SetTransparentProxy or
                TriggerActionKind.SetConnectionSampling =>
                new TriggerAction(kind, new BooleanActionParameters(true)),
            TriggerActionKind.SwitchProxyMode =>
                new TriggerAction(kind, new ProxyModeActionParameters(ClashSharpMode.RuleTakeover)),
            TriggerActionKind.SendNotification =>
                new TriggerAction(kind, new NotificationActionParameters(getString("Notification.Custom.Message"))),
            _ => throw new InvalidOperationException("Undefined trigger action kind."),
        };
        return new TriggerActionEditorViewModel(action, getString);
    }

    private TriggerActionParameters? Fail(string errorCode)
    {
        ErrorCode = errorCode;
        return null;
    }
}

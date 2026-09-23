using System;
using System.Globalization;
using System.Linq;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ViewModel;

/// <summary>Stable bindable row that publishes the latest persisted trigger definition.</summary>
internal sealed class TriggerTaskItemViewModel : ObservableObject
{
    private readonly Func<string, string> _getString;
    private bool _canMoveUp;
    private bool _canMoveDown;

    public bool CanMoveUp { get => _canMoveUp; internal set => SetProperty(ref _canMoveUp, value); }

    public bool CanMoveDown { get => _canMoveDown; internal set => SetProperty(ref _canMoveDown, value); }

    public TriggerTaskItemViewModel(
        TriggerTaskDefinition definition,
        DateTimeOffset? lastTriggeredAt,
        Func<string, string> getString)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        LastTriggeredAt = lastTriggeredAt;
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    public TriggerTaskDefinition Definition { get; private set; }

    public DateTimeOffset? LastTriggeredAt { get; private set; }

    /// <summary>Refreshes persisted values without replacing the row bound to an interactive control.</summary>
    public void Update(TriggerTaskDefinition definition, DateTimeOffset? lastTriggeredAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!StringComparer.Ordinal.Equals(Id, definition.Id))
        {
            throw new ArgumentException("A trigger row cannot change identity.", nameof(definition));
        }

        Definition = definition;
        LastTriggeredAt = lastTriggeredAt;
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ConditionsSummary));
        OnPropertyChanged(nameof(ActionsSummary));
        OnPropertyChanged(nameof(LastTriggeredAt));
        OnPropertyChanged(nameof(LastTriggeredSummary));
    }

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public bool IsEnabled => Definition.IsEnabled;

    /// <summary>Re-publishes the durable value after a rejected one-way UI toggle.</summary>
    public void RepublishEnabledState() => OnPropertyChanged(nameof(IsEnabled));

    public string ConditionsSummary => string.Join(", ", Definition.Conditions.Select(FormatCondition));

    public string ActionsSummary => string.Join(", ", Definition.Actions.Select(FormatAction));

    public string ConditionsLabel => _getString("Triggers.Conditions");

    public string ActionsLabel => _getString("Triggers.Actions");

    public string LastTriggeredLabel => _getString("Triggers.LastTriggered");

    /// <summary>Gets the localized accessible label for editing this trigger.</summary>
    public string EditText => _getString("Command.Edit");

    /// <summary>Gets the localized accessible label for moving this trigger earlier.</summary>
    public string MoveUpText => _getString("Command.MoveUp");

    /// <summary>Gets the localized accessible label for moving this trigger later.</summary>
    public string MoveDownText => _getString("Command.MoveDown");

    /// <summary>Gets the localized accessible label for deleting this trigger.</summary>
    public string DeleteText => _getString("Command.Delete");

    /// <summary>Gets the localized accessible label for the trigger-enabled switch.</summary>
    public string EnabledText => _getString("Triggers.Enabled.Title");

    public string LastTriggeredSummary => LastTriggeredAt is DateTimeOffset lastTriggered
        ? lastTriggered.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : _getString("Triggers.NeverTriggered");

    private string FormatCondition(TriggerCondition condition)
    {
        return condition.Parameters switch
        {
            EventConditionParameters eventParameters =>
                _getString($"Triggers.Condition.{eventParameters.EventKind}"),
            NotificationConditionParameters notification =>
                $"{_getString("Triggers.Condition.NotificationRaised")}: {notification.MinimumLevel}",
            TrafficConditionParameters traffic => FormatTrafficCondition(traffic),
            RateConditionParameters rate =>
                $"{_getString(rate.Direction == TriggerTrafficDirection.Upload
                    ? "Triggers.Condition.UploadRate"
                    : "Triggers.Condition.DownloadRate")} >= {FormatBytes(rate.ThresholdBytesPerSecond)}/s",
            ActiveConnectionsConditionParameters connections =>
                $"{_getString("Triggers.Condition.ActiveConnections")} >= {connections.Threshold:N0}",
            RuntimeConditionParameters runtime =>
                $"{_getString("Triggers.Condition.Runtime")} >= {runtime.Threshold:g}",
            SystemTimeConditionParameters time =>
                $"{_getString("Triggers.Condition.SystemTime")} >= {time.TargetTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')}",
            _ => _getString("Triggers.Validation.InvalidCondition"),
        };
    }

    private string FormatAction(TriggerAction action)
    {
        return _getString($"Triggers.Action.{action.Kind}");
    }

    private string TrafficTitle(TriggerTrafficScope scope)
    {
        return _getString(scope switch
        {
            TriggerTrafficScope.RollingWindow => "Triggers.Condition.TrafficInWindow",
            TriggerTrafficScope.CurrentSession => "Triggers.Condition.SessionTraffic",
            _ => "Triggers.Condition.TotalTraffic",
        });
    }

    private string FormatTrafficCondition(TrafficConditionParameters traffic)
    {
        string summary = $"{TrafficTitle(traffic.Scope)} >= {FormatBytes(traffic.ThresholdBytes)}";
        return traffic.Window is TimeSpan window
            ? $"{summary} · {window.ToString("g", CultureInfo.CurrentCulture)}"
            : summary;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N1} {units[unitIndex]}";
    }
}

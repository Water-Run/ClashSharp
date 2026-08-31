using System;
using System.Globalization;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ViewModel;

/// <summary>Owns one editable typed trigger-condition draft and its field validation.</summary>
internal sealed class TriggerConditionEditorViewModel : ObservableObject
{
    private readonly Func<string, string> _getString;
    private TriggerTrafficScope _trafficScope;
    private TriggerTrafficDirection _rateDirection;
    private TriggerNotificationLevel _notificationLevel;
    private string _thresholdText;
    private string _windowSecondsText;
    private string _runtimeSecondsText;
    private string _targetTimeText;
    private string? _errorCode;

    /// <summary>Initializes a draft that preserves every typed parameter from an existing condition.</summary>
    public TriggerConditionEditorViewModel(
        TriggerCondition condition,
        Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        Id = condition.Id;
        Kind = condition.Kind;
        EventKind = TriggerEventKind.AppEntered;
        _trafficScope = TriggerTrafficScope.RollingWindow;
        _rateDirection = TriggerTrafficDirection.Upload;
        _notificationLevel = TriggerNotificationLevel.Default;
        _thresholdText = "1";
        _windowSecondsText = "300";
        _runtimeSecondsText = "60";
        _targetTimeText = "23:00";

        switch (condition.Parameters)
        {
            case EventConditionParameters parameters:
                EventKind = parameters.EventKind;
                break;
            case NotificationConditionParameters parameters:
                _notificationLevel = parameters.MinimumLevel;
                break;
            case TrafficConditionParameters parameters:
                _trafficScope = parameters.Scope;
                _thresholdText = parameters.ThresholdBytes.ToString(CultureInfo.InvariantCulture);
                if (parameters.Window is TimeSpan window)
                {
                    _windowSecondsText = FormatSeconds(window);
                }

                break;
            case RateConditionParameters parameters:
                _rateDirection = parameters.Direction;
                _thresholdText = parameters.ThresholdBytesPerSecond.ToString(CultureInfo.InvariantCulture);
                break;
            case ActiveConnectionsConditionParameters parameters:
                _thresholdText = parameters.Threshold.ToString(CultureInfo.InvariantCulture);
                break;
            case RuntimeConditionParameters parameters:
                _runtimeSecondsText = FormatSeconds(parameters.Threshold);
                break;
            case SystemTimeConditionParameters parameters:
                _targetTimeText = parameters.TargetTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
                    .TrimEnd('0')
                    .TrimEnd('.');
                break;
            default:
                throw new ArgumentException(
                    "The condition contains an unsupported parameter shape.",
                    nameof(condition));
        }
    }

    /// <summary>Gets the stable condition identity preserved across editing.</summary>
    public string Id { get; }

    /// <summary>Gets the typed condition parameter shape.</summary>
    public TriggerConditionKind Kind { get; }

    /// <summary>Gets the exact application event for an event condition.</summary>
    public TriggerEventKind EventKind { get; }

    public string Title => _getString($"Triggers.Condition.{GetLocalizationSuffix()}");

    public string Description => _getString($"Triggers.Condition.{GetLocalizationSuffix()}.Description");

    /// <summary>Gets the localized accessible label for moving this condition earlier.</summary>
    public string MoveUpText => _getString("Command.MoveUp");

    /// <summary>Gets the localized accessible label for moving this condition later.</summary>
    public string MoveDownText => _getString("Command.MoveDown");

    /// <summary>Gets the localized accessible label for removing this condition.</summary>
    public string RemoveText => _getString("Command.Delete");

    public bool IsThresholdVisible => Kind is TriggerConditionKind.Traffic
        or TriggerConditionKind.Rate
        or TriggerConditionKind.ActiveConnections;

    public bool IsWindowVisible => Kind == TriggerConditionKind.Traffic
        && TrafficScope == TriggerTrafficScope.RollingWindow;

    public bool IsRuntimeVisible => Kind == TriggerConditionKind.Runtime;

    public bool IsTimeVisible => Kind == TriggerConditionKind.SystemTime;

    public bool IsNotificationLevelVisible => Kind == TriggerConditionKind.Notification;

    public bool IsTrafficScopeVisible => Kind == TriggerConditionKind.Traffic;

    public bool IsRateDirectionVisible => Kind == TriggerConditionKind.Rate;

    public string ThresholdUnitText => Kind == TriggerConditionKind.Rate ? "B/s" :
        Kind == TriggerConditionKind.ActiveConnections ? _getString("Triggers.Condition.Unit.Count") : "B";

    public TriggerTrafficScope TrafficScope
    {
        get => _trafficScope;
        set
        {
            if (SetProperty(ref _trafficScope, value))
            {
                ClearError();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(IsWindowVisible));
            }
        }
    }

    public TriggerTrafficDirection RateDirection
    {
        get => _rateDirection;
        set
        {
            if (SetProperty(ref _rateDirection, value))
            {
                ClearError();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Description));
            }
        }
    }

    public TriggerNotificationLevel NotificationLevel
    {
        get => _notificationLevel;
        set
        {
            if (SetProperty(ref _notificationLevel, value))
            {
                ClearError();
            }
        }
    }

    public string ThresholdText
    {
        get => _thresholdText;
        set
        {
            if (SetProperty(ref _thresholdText, value ?? string.Empty))
            {
                ClearError();
            }
        }
    }

    public string WindowSecondsText
    {
        get => _windowSecondsText;
        set
        {
            if (SetProperty(ref _windowSecondsText, value ?? string.Empty))
            {
                ClearError();
            }
        }
    }

    public string RuntimeSecondsText
    {
        get => _runtimeSecondsText;
        set
        {
            if (SetProperty(ref _runtimeSecondsText, value ?? string.Empty))
            {
                ClearError();
            }
        }
    }

    public string TargetTimeText
    {
        get => _targetTimeText;
        set
        {
            if (SetProperty(ref _targetTimeText, value ?? string.Empty))
            {
                ClearError();
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

    public string? ErrorMessage => ErrorCode is null ? null : _getString(MapErrorResource(ErrorCode));

    public bool HasError => ErrorCode is not null;

    /// <summary>Creates a validated domain condition without presentation unit conversion in the view.</summary>
    public bool TryBuild(out TriggerCondition? condition)
    {
        condition = null;
        ErrorCode = null;
        TriggerConditionParameters? parameters = Kind switch
        {
            TriggerConditionKind.Event => BuildEvent(),
            TriggerConditionKind.Notification => BuildNotification(),
            TriggerConditionKind.Traffic => BuildTraffic(),
            TriggerConditionKind.Rate => BuildRate(),
            TriggerConditionKind.ActiveConnections => BuildConnections(),
            TriggerConditionKind.Runtime => BuildRuntime(),
            TriggerConditionKind.SystemTime => BuildSystemTime(),
            _ => Fail("trigger.condition.kind.undefined"),
        };
        if (parameters is null)
        {
            return false;
        }

        condition = new TriggerCondition(Id, Kind, parameters);
        return true;
    }

    /// <summary>Creates one default typed condition template with a fresh stable identity.</summary>
    public static TriggerConditionEditorViewModel Create(
        TriggerConditionTemplate template,
        Func<string, string> getString,
        string? id = null)
    {
        ArgumentNullException.ThrowIfNull(getString);
        if (!Enum.IsDefined(template))
        {
            throw new ArgumentOutOfRangeException(nameof(template));
        }

        string conditionId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        TriggerCondition condition = template switch
        {
            TriggerConditionTemplate.AppEntered => new(
                conditionId,
                TriggerConditionKind.Event,
                new EventConditionParameters(TriggerEventKind.AppEntered)),
            TriggerConditionTemplate.ProxyStarted => new(
                conditionId,
                TriggerConditionKind.Event,
                new EventConditionParameters(TriggerEventKind.ProxyStarted)),
            TriggerConditionTemplate.NotificationRaised => new(
                conditionId,
                TriggerConditionKind.Notification,
                new NotificationConditionParameters(TriggerNotificationLevel.CriticalOnly)),
            TriggerConditionTemplate.RollingTraffic => new(
                conditionId,
                TriggerConditionKind.Traffic,
                new TrafficConditionParameters(
                    TriggerTrafficScope.RollingWindow,
                    100L * 1024L * 1024L,
                    TimeSpan.FromMinutes(5))),
            TriggerConditionTemplate.SessionTraffic => new(
                conditionId,
                TriggerConditionKind.Traffic,
                new TrafficConditionParameters(TriggerTrafficScope.CurrentSession, 1024L * 1024L * 1024L)),
            TriggerConditionTemplate.AllTimeTraffic => new(
                conditionId,
                TriggerConditionKind.Traffic,
                new TrafficConditionParameters(TriggerTrafficScope.AllTime, 1024L * 1024L * 1024L)),
            TriggerConditionTemplate.UploadRate => new(
                conditionId,
                TriggerConditionKind.Rate,
                new RateConditionParameters(TriggerTrafficDirection.Upload, 1024L * 1024L)),
            TriggerConditionTemplate.DownloadRate => new(
                conditionId,
                TriggerConditionKind.Rate,
                new RateConditionParameters(TriggerTrafficDirection.Download, 1024L * 1024L)),
            TriggerConditionTemplate.ActiveConnections => new(
                conditionId,
                TriggerConditionKind.ActiveConnections,
                new ActiveConnectionsConditionParameters(20)),
            TriggerConditionTemplate.Runtime => new(
                conditionId,
                TriggerConditionKind.Runtime,
                new RuntimeConditionParameters(TimeSpan.FromHours(1))),
            TriggerConditionTemplate.SystemTime => new(
                conditionId,
                TriggerConditionKind.SystemTime,
                new SystemTimeConditionParameters(new TimeOnly(23, 0))),
            _ => throw new InvalidOperationException("Undefined trigger condition template."),
        };
        return new TriggerConditionEditorViewModel(condition, getString);
    }

    private TriggerConditionParameters? BuildEvent()
    {
        return Enum.IsDefined(EventKind)
            && EventKind is TriggerEventKind.AppEntered or TriggerEventKind.ProxyStarted
                ? new EventConditionParameters(EventKind)
                : Fail("trigger.condition.event.invalid");
    }

    private TriggerConditionParameters? BuildNotification()
    {
        return Enum.IsDefined(NotificationLevel)
            ? new NotificationConditionParameters(NotificationLevel)
            : Fail("trigger.condition.notification.level.undefined");
    }

    private TriggerConditionParameters? BuildTraffic()
    {
        if (!Enum.IsDefined(TrafficScope))
        {
            return Fail("trigger.condition.traffic.scope.undefined");
        }

        if (!TryParsePositiveInt64(ThresholdText, out long threshold))
        {
            return Fail("trigger.condition.threshold.invalid");
        }

        if (TrafficScope != TriggerTrafficScope.RollingWindow)
        {
            return new TrafficConditionParameters(TrafficScope, threshold);
        }

        return TryParsePositiveDuration(WindowSecondsText, out TimeSpan window)
            ? new TrafficConditionParameters(TrafficScope, threshold, window)
            : Fail("trigger.condition.window.invalid");
    }

    private TriggerConditionParameters? BuildRate()
    {
        if (!Enum.IsDefined(RateDirection))
        {
            return Fail("trigger.condition.rate.direction.undefined");
        }

        return TryParsePositiveInt64(ThresholdText, out long threshold)
            ? new RateConditionParameters(RateDirection, threshold)
            : Fail("trigger.condition.threshold.invalid");
    }

    private TriggerConditionParameters? BuildConnections()
    {
        return (int.TryParse(
                    ThresholdText,
                    NumberStyles.Integer,
                    CultureInfo.CurrentCulture,
                    out int threshold)
                || int.TryParse(
                    ThresholdText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out threshold))
            && threshold > 0
                ? new ActiveConnectionsConditionParameters(threshold)
                : Fail("trigger.condition.threshold.invalid");
    }

    private TriggerConditionParameters? BuildRuntime()
    {
        return TryParsePositiveDuration(RuntimeSecondsText, out TimeSpan duration)
            ? new RuntimeConditionParameters(duration)
            : Fail("trigger.condition.threshold.invalid");
    }

    private TriggerConditionParameters? BuildSystemTime()
    {
        return (TimeOnly.TryParse(TargetTimeText, CultureInfo.CurrentCulture, out TimeOnly target)
                || TimeOnly.TryParse(TargetTimeText, CultureInfo.InvariantCulture, out target))
            ? new SystemTimeConditionParameters(target)
            : Fail("trigger.condition.time.invalid");
    }

    private TriggerConditionParameters? Fail(string errorCode)
    {
        ErrorCode = errorCode;
        return null;
    }

    private string GetLocalizationSuffix()
    {
        return Kind switch
        {
            TriggerConditionKind.Event when EventKind == TriggerEventKind.ProxyStarted => "ProxyStarted",
            TriggerConditionKind.Event => "AppEntered",
            TriggerConditionKind.Notification => "NotificationRaised",
            TriggerConditionKind.Traffic when TrafficScope == TriggerTrafficScope.RollingWindow => "TrafficInWindow",
            TriggerConditionKind.Traffic when TrafficScope == TriggerTrafficScope.CurrentSession => "SessionTraffic",
            TriggerConditionKind.Traffic => "TotalTraffic",
            TriggerConditionKind.Rate when RateDirection == TriggerTrafficDirection.Download => "DownloadRate",
            TriggerConditionKind.Rate => "UploadRate",
            TriggerConditionKind.ActiveConnections => "ActiveConnections",
            TriggerConditionKind.Runtime => "Runtime",
            TriggerConditionKind.SystemTime => "SystemTime",
            _ => "AppEntered",
        };
    }

    private void ClearError()
    {
        ErrorCode = null;
    }

    private static bool TryParsePositiveInt64(string text, out long value)
    {
        return (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
                || long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            && value > 0;
    }

    private static bool TryParsePositiveDuration(string text, out TimeSpan duration)
    {
        const NumberStyles durationStyles = NumberStyles.AllowLeadingWhite
            | NumberStyles.AllowTrailingWhite
            | NumberStyles.AllowLeadingSign
            | NumberStyles.AllowDecimalPoint;
        duration = default;
        if ((!decimal.TryParse(text, durationStyles, CultureInfo.CurrentCulture, out decimal seconds)
                && !decimal.TryParse(text, durationStyles, CultureInfo.InvariantCulture, out seconds))
            || seconds <= 0)
        {
            return false;
        }

        decimal ticks = seconds * TimeSpan.TicksPerSecond;
        if (ticks != decimal.Truncate(ticks)
            || ticks > TimeSpan.MaxValue.Ticks
            || ticks < TimeSpan.MinValue.Ticks)
        {
            return false;
        }

        duration = TimeSpan.FromTicks(decimal.ToInt64(ticks));
        return duration > TimeSpan.Zero;
    }

    private static string FormatSeconds(TimeSpan duration)
    {
        decimal seconds = (decimal)duration.Ticks / TimeSpan.TicksPerSecond;
        return seconds.ToString(CultureInfo.CurrentCulture);
    }

    private string MapErrorResource(string errorCode)
    {
        return errorCode switch
        {
            "trigger.condition.time.invalid" => "Triggers.Validation.TimeFormat",
            "trigger.condition.window.invalid" => "Triggers.Validation.PositiveRuntime",
            "trigger.condition.threshold.invalid"
                when Kind == TriggerConditionKind.ActiveConnections =>
                    "Triggers.Validation.PositiveCount",
            "trigger.condition.threshold.invalid"
                when Kind == TriggerConditionKind.Runtime =>
                    "Triggers.Validation.PositiveRuntime",
            "trigger.condition.threshold.invalid" => "Triggers.Validation.PositiveTraffic",
            _ => "Triggers.Validation.InvalidCondition",
        };
    }
}

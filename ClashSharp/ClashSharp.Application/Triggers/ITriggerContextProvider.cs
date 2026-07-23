using System.Collections.ObjectModel;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Immutable request for only the observations required by one trigger definition.</summary>
public sealed class TriggerContextRequest
{
    /// <summary>Initializes one validated trigger context request.</summary>
    public TriggerContextRequest(
        TriggerEventKind eventKind,
        TriggerNotificationLevel? notificationLevel,
        IEnumerable<TriggerDataField> requiredFields,
        IEnumerable<TimeSpan> rollingWindows)
    {
        if (!Enum.IsDefined(eventKind))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        if (notificationLevel is TriggerNotificationLevel level && !Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(notificationLevel));
        }

        ArgumentNullException.ThrowIfNull(requiredFields);
        ArgumentNullException.ThrowIfNull(rollingWindows);
        TriggerDataField[] fields = requiredFields.Distinct().Order().ToArray();
        if (fields.Any(field => !Enum.IsDefined(field)))
        {
            throw new ArgumentException("Required fields must be defined enum values.", nameof(requiredFields));
        }

        TimeSpan[] windows = rollingWindows.Distinct().Order().ToArray();
        if (windows.Any(window => window <= TimeSpan.Zero)
            || (fields.Contains(TriggerDataField.RollingTraffic) != (windows.Length > 0)))
        {
            throw new ArgumentException(
                "Rolling traffic requires one or more positive exact windows.",
                nameof(rollingWindows));
        }

        EventKind = eventKind;
        NotificationLevel = notificationLevel;
        RequiredFields = Array.AsReadOnly(fields);
        RollingWindows = Array.AsReadOnly(windows);
    }

    /// <summary>Gets the runtime event requesting evaluation.</summary>
    public TriggerEventKind EventKind { get; }

    /// <summary>Gets notification severity carried by the event, when present.</summary>
    public TriggerNotificationLevel? NotificationLevel { get; }

    /// <summary>Gets the exact independently available observations to acquire.</summary>
    public ReadOnlyCollection<TriggerDataField> RequiredFields { get; }

    /// <summary>Gets exact positive rolling durations required by traffic conditions.</summary>
    public ReadOnlyCollection<TimeSpan> RollingWindows { get; }
}

/// <summary>Asynchronously acquires immutable trigger observations without blocking the caller.</summary>
public interface ITriggerContextProvider
{
    /// <summary>Acquires requested fields and classifies expected source failures.</summary>
    Task<TriggerContextResult> AcquireAsync(
        TriggerContextRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Builds minimal requests and verifies whether degraded data supports a sound decision.</summary>
public sealed class TriggerContextAcquirer
{
    private readonly ITriggerContextProvider _provider;

    /// <summary>Initializes an acquirer over one host-composed provider.</summary>
    public TriggerContextAcquirer(ITriggerContextProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>Acquires only fields required by one enabled definition and classifies soundness.</summary>
    public async Task<TriggerContextResult> AcquireAsync(
        TriggerTaskDefinition definition,
        TriggerTaskState state,
        TriggerEventKind eventKind,
        TriggerNotificationLevel? notificationLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        if (!TriggerDefinitionValidator.Validate(definition).IsValid
            || !StringComparer.Ordinal.Equals(definition.Id, state.TaskId))
        {
            throw new ArgumentException("A valid definition and its matching state are required.");
        }

        if (!Enum.IsDefined(eventKind))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        if (notificationLevel is TriggerNotificationLevel level && !Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(notificationLevel));
        }

        if (!definition.IsEnabled)
        {
            return TriggerContextResult.NotRequired("trigger.context.disabled");
        }

        TriggerContextRequest request = CreateRequest(
            definition,
            eventKind,
            notificationLevel);
        TriggerContextResult result = await _provider.AcquireAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (result.Status != TriggerContextStatus.Degraded || result.Context is null)
        {
            return result;
        }

        TriggerMatchDecision decision = TriggerMatcher.Evaluate(
            definition,
            state,
            result.Context);
        return decision.Outcome == TriggerMatchOutcome.InsufficientData
            ? result.AsUnsound("trigger.context.unsound_decision")
            : result;
    }

    private static TriggerContextRequest CreateRequest(
        TriggerTaskDefinition definition,
        TriggerEventKind eventKind,
        TriggerNotificationLevel? notificationLevel)
    {
        HashSet<TriggerDataField> fields = [];
        HashSet<TimeSpan> rollingWindows = [];
        foreach (TriggerCondition condition in definition.Conditions)
        {
            switch (condition.Parameters)
            {
                case NotificationConditionParameters
                    when eventKind == TriggerEventKind.NotificationRaised:
                    fields.Add(TriggerDataField.NotificationLevel);
                    break;
                case TrafficConditionParameters { Scope: TriggerTrafficScope.RollingWindow, Window: TimeSpan window }:
                    fields.Add(TriggerDataField.RollingTraffic);
                    rollingWindows.Add(window);
                    break;
                case TrafficConditionParameters { Scope: TriggerTrafficScope.CurrentSession }:
                    fields.Add(TriggerDataField.CurrentSessionTraffic);
                    break;
                case TrafficConditionParameters { Scope: TriggerTrafficScope.AllTime }:
                    fields.Add(TriggerDataField.AllTimeTraffic);
                    break;
                case RateConditionParameters { Direction: TriggerTrafficDirection.Upload }:
                    fields.Add(TriggerDataField.UploadBytesPerSecond);
                    break;
                case RateConditionParameters { Direction: TriggerTrafficDirection.Download }:
                    fields.Add(TriggerDataField.DownloadBytesPerSecond);
                    break;
                case ActiveConnectionsConditionParameters:
                    fields.Add(TriggerDataField.ActiveConnectionCount);
                    break;
                case RuntimeConditionParameters:
                    fields.Add(TriggerDataField.Runtime);
                    break;
                case SystemTimeConditionParameters:
                    fields.Add(TriggerDataField.LocalDate);
                    fields.Add(TriggerDataField.LocalTime);
                    break;
            }
        }

        return new TriggerContextRequest(
            eventKind,
            notificationLevel,
            fields,
            rollingWindows);
    }
}

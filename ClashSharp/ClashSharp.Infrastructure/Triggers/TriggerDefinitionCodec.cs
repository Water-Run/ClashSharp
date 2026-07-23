using System.Text.Json;
using ClashSharp.Model.Triggers;

namespace ClashSharp.Infrastructure.Triggers;

internal static class TriggerDefinitionCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string SerializeConditionParameters(TriggerCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition.Kind switch
        {
            TriggerConditionKind.Event => Serialize<EventConditionParameters>(condition.Parameters),
            TriggerConditionKind.Notification =>
                Serialize<NotificationConditionParameters>(condition.Parameters),
            TriggerConditionKind.Traffic => Serialize<TrafficConditionParameters>(condition.Parameters),
            TriggerConditionKind.Rate => Serialize<RateConditionParameters>(condition.Parameters),
            TriggerConditionKind.ActiveConnections =>
                Serialize<ActiveConnectionsConditionParameters>(condition.Parameters),
            TriggerConditionKind.Runtime => Serialize<RuntimeConditionParameters>(condition.Parameters),
            TriggerConditionKind.SystemTime =>
                Serialize<SystemTimeConditionParameters>(condition.Parameters),
            _ => throw new InvalidDataException("Undefined trigger condition kind."),
        };
    }

    public static TriggerConditionParameters DeserializeConditionParameters(
        TriggerConditionKind kind,
        string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return kind switch
            {
                TriggerConditionKind.Event => Deserialize<EventConditionParameters>(json),
                TriggerConditionKind.Notification =>
                    Deserialize<NotificationConditionParameters>(json),
                TriggerConditionKind.Traffic => Deserialize<TrafficConditionParameters>(json),
                TriggerConditionKind.Rate => Deserialize<RateConditionParameters>(json),
                TriggerConditionKind.ActiveConnections =>
                    Deserialize<ActiveConnectionsConditionParameters>(json),
                TriggerConditionKind.Runtime => Deserialize<RuntimeConditionParameters>(json),
                TriggerConditionKind.SystemTime => Deserialize<SystemTimeConditionParameters>(json),
                _ => throw new InvalidDataException("Undefined trigger condition kind."),
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Trigger condition parameters are malformed.", exception);
        }
    }

    public static string SerializeActionParameters(TriggerAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            TriggerActionKind.CloseConnections or TriggerActionKind.ExitApplication =>
                Serialize<NoActionParameters>(action.Parameters),
            TriggerActionKind.SetLaunchAtStartup or
                TriggerActionKind.SetTransparentProxy or
                TriggerActionKind.SetConnectionSampling =>
                Serialize<BooleanActionParameters>(action.Parameters),
            TriggerActionKind.SwitchProxyMode => Serialize<ProxyModeActionParameters>(action.Parameters),
            TriggerActionKind.SendNotification =>
                Serialize<NotificationActionParameters>(action.Parameters),
            _ => throw new InvalidDataException("Undefined trigger action kind."),
        };
    }

    public static TriggerActionParameters DeserializeActionParameters(
        TriggerActionKind kind,
        string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return kind switch
            {
                TriggerActionKind.CloseConnections or TriggerActionKind.ExitApplication =>
                    Deserialize<NoActionParameters>(json),
                TriggerActionKind.SetLaunchAtStartup or
                    TriggerActionKind.SetTransparentProxy or
                    TriggerActionKind.SetConnectionSampling =>
                    Deserialize<BooleanActionParameters>(json),
                TriggerActionKind.SwitchProxyMode => Deserialize<ProxyModeActionParameters>(json),
                TriggerActionKind.SendNotification =>
                    Deserialize<NotificationActionParameters>(json),
                _ => throw new InvalidDataException("Undefined trigger action kind."),
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Trigger action parameters are malformed.", exception);
        }
    }

    private static string Serialize<T>(object parameters)
    {
        if (parameters is not T typedParameters)
        {
            throw new InvalidDataException("Trigger parameter type does not match its kind.");
        }

        return JsonSerializer.Serialize(typedParameters, SerializerOptions);
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new InvalidDataException("Trigger parameter payload is null.");
    }
}

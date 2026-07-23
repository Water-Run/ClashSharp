using System.Collections.ObjectModel;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Identifies whether trigger context acquisition can support a sound decision.</summary>
public enum TriggerContextStatus
{
    /// <summary>Every requested field is available.</summary>
    Available = 0,

    /// <summary>Some fields are unavailable, but the current AND decision remains sound.</summary>
    Degraded = 1,

    /// <summary>Missing fields prevent a sound match or latch transition decision.</summary>
    Unsound = 2,

    /// <summary>No context is required because evaluation is disabled or already definitely false.</summary>
    NotRequired = 3,
}

/// <summary>Typed result of one asynchronous trigger context acquisition.</summary>
public sealed class TriggerContextResult
{
    private TriggerContextResult(
        TriggerContextStatus status,
        TriggerEvaluationContext? context,
        IReadOnlyDictionary<TriggerDataField, TriggerDataUnavailableReason> unavailableFields,
        string? diagnosticCode)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentNullException.ThrowIfNull(unavailableFields);
        Dictionary<TriggerDataField, TriggerDataUnavailableReason> unavailable = new();
        foreach ((TriggerDataField field, TriggerDataUnavailableReason reason) in unavailableFields)
        {
            if (!Enum.IsDefined(field) || !Enum.IsDefined(reason))
            {
                throw new ArgumentException(
                    "Unavailable fields and reasons must be defined enum values.",
                    nameof(unavailableFields));
            }

            unavailable.Add(field, reason);
        }

        bool validShape = status switch
        {
            TriggerContextStatus.Available => context is not null
                && unavailable.Count == 0
                && diagnosticCode is null,
            TriggerContextStatus.Degraded => context is not null
                && unavailable.Count > 0
                && !string.IsNullOrWhiteSpace(diagnosticCode),
            TriggerContextStatus.Unsound => context is not null
                && unavailable.Count > 0
                && !string.IsNullOrWhiteSpace(diagnosticCode),
            TriggerContextStatus.NotRequired => context is null
                && unavailable.Count == 0
                && !string.IsNullOrWhiteSpace(diagnosticCode),
            _ => false,
        };
        if (!validShape)
        {
            throw new ArgumentException("Trigger context result fields do not match its status.");
        }

        Status = status;
        Context = context;
        UnavailableFields = new ReadOnlyDictionary<TriggerDataField, TriggerDataUnavailableReason>(
            unavailable);
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the typed acquisition status.</summary>
    public TriggerContextStatus Status { get; }

    /// <summary>Gets the immutable context when acquisition performed work.</summary>
    public TriggerEvaluationContext? Context { get; }

    /// <summary>Gets unavailable requested fields and their stable reasons.</summary>
    public ReadOnlyDictionary<TriggerDataField, TriggerDataUnavailableReason> UnavailableFields { get; }

    /// <summary>Gets a stable diagnostic code for non-fully-available outcomes.</summary>
    public string? DiagnosticCode { get; }

    /// <summary>Creates a fully available context result.</summary>
    public static TriggerContextResult Available(TriggerEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new TriggerContextResult(
            TriggerContextStatus.Available,
            context,
            new Dictionary<TriggerDataField, TriggerDataUnavailableReason>(),
            null);
    }

    /// <summary>Creates a context with one or more unavailable fields.</summary>
    public static TriggerContextResult Degraded(
        TriggerEvaluationContext context,
        IReadOnlyDictionary<TriggerDataField, TriggerDataUnavailableReason> unavailableFields)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new TriggerContextResult(
            TriggerContextStatus.Degraded,
            context,
            unavailableFields,
            "trigger.context.degraded");
    }

    /// <summary>Creates a no-work result without invoking external context sources.</summary>
    public static TriggerContextResult NotRequired(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        return new TriggerContextResult(
            TriggerContextStatus.NotRequired,
            null,
            new Dictionary<TriggerDataField, TriggerDataUnavailableReason>(),
            diagnosticCode);
    }

    internal TriggerContextResult AsUnsound(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        if (Status != TriggerContextStatus.Degraded || Context is null)
        {
            throw new InvalidOperationException("Only a degraded acquired context can become unsound.");
        }

        return new TriggerContextResult(
            TriggerContextStatus.Unsound,
            Context,
            UnavailableFields,
            diagnosticCode);
    }
}

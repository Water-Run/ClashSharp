namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Identifies a typed trigger persistence outcome.</summary>
public enum TriggerPersistenceStatus
{
    /// <summary>The operation committed successfully.</summary>
    Succeeded = 0,

    /// <summary>An optimistic generation, revision, version, or expected state changed.</summary>
    Conflict = 1,

    /// <summary>The requested durable entity does not exist.</summary>
    NotFound = 2,

    /// <summary>The request failed validation before persistence.</summary>
    Invalid = 3,

    /// <summary>Storage was unavailable or inconclusive.</summary>
    Unavailable = 4,
}

/// <summary>Typed trigger persistence result without a value payload.</summary>
public sealed class TriggerPersistenceResult
{
    private TriggerPersistenceResult(
        TriggerPersistenceStatus status,
        TriggerDiagnostic? diagnostic)
    {
        Status = status;
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the typed persistence status.</summary>
    public TriggerPersistenceStatus Status { get; }

    /// <summary>Gets an optional durable diagnostic.</summary>
    public TriggerDiagnostic? Diagnostic { get; }

    /// <summary>Gets whether the operation committed successfully.</summary>
    public bool IsSucceeded => Status == TriggerPersistenceStatus.Succeeded;

    /// <summary>Creates a successful result.</summary>
    public static TriggerPersistenceResult Succeeded()
    {
        return new TriggerPersistenceResult(TriggerPersistenceStatus.Succeeded, null);
    }

    /// <summary>Creates an optimistic-conflict result.</summary>
    public static TriggerPersistenceResult Conflict()
    {
        return new TriggerPersistenceResult(TriggerPersistenceStatus.Conflict, null);
    }

    /// <summary>Creates a not-found result.</summary>
    public static TriggerPersistenceResult NotFound()
    {
        return new TriggerPersistenceResult(TriggerPersistenceStatus.NotFound, null);
    }

    /// <summary>Creates an invalid-request result.</summary>
    /// <param name="diagnostic">Diagnostic describing invalid input.</param>
    public static TriggerPersistenceResult Invalid(TriggerDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new TriggerPersistenceResult(TriggerPersistenceStatus.Invalid, diagnostic);
    }

    /// <summary>Creates an unavailable-storage result.</summary>
    /// <param name="diagnostic">Diagnostic describing unavailable storage.</param>
    public static TriggerPersistenceResult Unavailable(TriggerDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new TriggerPersistenceResult(TriggerPersistenceStatus.Unavailable, diagnostic);
    }

    /// <summary>Creates a successful result with a value payload.</summary>
    /// <typeparam name="T">Committed or observed value type.</typeparam>
    /// <param name="value">Committed or observed value.</param>
    public static TriggerPersistenceResult<T> Succeeded<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new TriggerPersistenceResult<T>(TriggerPersistenceStatus.Succeeded, value, null);
    }

    /// <summary>Creates an optimistic-conflict result with no value payload.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    public static TriggerPersistenceResult<T> Conflict<T>()
    {
        return new TriggerPersistenceResult<T>(TriggerPersistenceStatus.Conflict, default, null);
    }

    /// <summary>Creates a not-found result with no value payload.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    public static TriggerPersistenceResult<T> NotFound<T>()
    {
        return new TriggerPersistenceResult<T>(TriggerPersistenceStatus.NotFound, default, null);
    }

    /// <summary>Creates an invalid-request result with no value payload.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="diagnostic">Diagnostic describing invalid input.</param>
    public static TriggerPersistenceResult<T> Invalid<T>(TriggerDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new TriggerPersistenceResult<T>(TriggerPersistenceStatus.Invalid, default, diagnostic);
    }

    /// <summary>Creates an unavailable-storage result with no value payload.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="diagnostic">Diagnostic describing unavailable storage.</param>
    public static TriggerPersistenceResult<T> Unavailable<T>(TriggerDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new TriggerPersistenceResult<T>(TriggerPersistenceStatus.Unavailable, default, diagnostic);
    }
}

/// <summary>Typed trigger persistence result with an immutable value payload.</summary>
/// <typeparam name="T">Committed or observed value type.</typeparam>
public sealed class TriggerPersistenceResult<T>
{
    internal TriggerPersistenceResult(
        TriggerPersistenceStatus status,
        T? value,
        TriggerDiagnostic? diagnostic)
    {
        Status = status;
        Value = value;
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the typed persistence status.</summary>
    public TriggerPersistenceStatus Status { get; }

    /// <summary>Gets the committed or observed value when successful.</summary>
    public T? Value { get; }

    /// <summary>Gets an optional durable diagnostic.</summary>
    public TriggerDiagnostic? Diagnostic { get; }

    /// <summary>Gets whether the operation committed successfully.</summary>
    public bool IsSucceeded => Status == TriggerPersistenceStatus.Succeeded;

}

using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Classifies one expected settings persistence outcome.</summary>
public enum SettingsPersistenceStatus
{
    /// <summary>The requested read or replacement completed successfully.</summary>
    Succeeded = 0,

    /// <summary>The persisted revision no longer matches the caller's expectation.</summary>
    Conflict = 1,

    /// <summary>The proposed envelope or request violates a stable invariant.</summary>
    Invalid = 2,

    /// <summary>Storage is temporarily inaccessible, denied, or busy.</summary>
    Unavailable = 3,

    /// <summary>No trustworthy primary or backup envelope can be decoded.</summary>
    Corrupt = 4,
}

/// <summary>Contains one stable, nonlocalized settings persistence diagnostic.</summary>
public sealed record SettingsPersistenceDiagnostic
{
    /// <summary>Initializes a path-addressed diagnostic.</summary>
    public SettingsPersistenceDiagnostic(string code, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Code = code;
        Path = path;
    }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the stable logical or repository path.</summary>
    public string Path { get; }
}

/// <summary>Contains a verified envelope or a typed non-throwing persistence failure.</summary>
public sealed class SettingsPersistenceResult
{
    private SettingsPersistenceResult(
        SettingsPersistenceStatus status,
        SettingsEnvelope? envelope,
        bool recoveredFromBackup,
        SettingsPersistenceDiagnostic? diagnostic)
    {
        Status = status;
        Envelope = envelope;
        RecoveredFromBackup = recoveredFromBackup;
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the typed persistence status.</summary>
    public SettingsPersistenceStatus Status { get; }

    /// <summary>Gets the committed or observed envelope when available.</summary>
    public SettingsEnvelope? Envelope { get; }

    /// <summary>Gets whether open restored the primary from a verified backup.</summary>
    public bool RecoveredFromBackup { get; }

    /// <summary>Gets the stable diagnostic for invalid, unavailable, corrupt, or recovered state.</summary>
    public SettingsPersistenceDiagnostic? Diagnostic { get; }

    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool IsSucceeded => Status == SettingsPersistenceStatus.Succeeded;

    /// <summary>Creates a successful empty or envelope-bearing result.</summary>
    public static SettingsPersistenceResult Succeeded(
        SettingsEnvelope? envelope = null,
        bool recoveredFromBackup = false,
        SettingsPersistenceDiagnostic? diagnostic = null)
    {
        if (recoveredFromBackup && envelope is null)
        {
            throw new ArgumentException(
                "Backup recovery must produce a verified envelope.",
                nameof(envelope));
        }

        return new SettingsPersistenceResult(
            SettingsPersistenceStatus.Succeeded,
            envelope,
            recoveredFromBackup,
            diagnostic);
    }

    /// <summary>Creates an optimistic conflict with the currently verified envelope, if any.</summary>
    public static SettingsPersistenceResult Conflict(SettingsEnvelope? current) =>
        new(SettingsPersistenceStatus.Conflict, current, false, null);

    /// <summary>Creates an invalid-request result.</summary>
    public static SettingsPersistenceResult Invalid(
        SettingsPersistenceDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new SettingsPersistenceResult(
            SettingsPersistenceStatus.Invalid,
            null,
            false,
            diagnostic);
    }

    /// <summary>Creates an unavailable-storage result.</summary>
    public static SettingsPersistenceResult Unavailable(
        SettingsPersistenceDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new SettingsPersistenceResult(
            SettingsPersistenceStatus.Unavailable,
            null,
            false,
            diagnostic);
    }

    /// <summary>Creates a corrupt-storage result.</summary>
    public static SettingsPersistenceResult Corrupt(
        SettingsPersistenceDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new SettingsPersistenceResult(
            SettingsPersistenceStatus.Corrupt,
            null,
            false,
            diagnostic);
    }
}

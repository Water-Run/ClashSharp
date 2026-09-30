namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Replaces settings through a complete, recoverable data transaction.</summary>
public interface ISettingsDataReplacement
{
    /// <summary>Validates and imports a package, retaining ownership through runtime publication.</summary>
    Task<SettingsDataReplacementResult> ImportAsync(string packagePath, CancellationToken cancellationToken);

    /// <summary>Restores all settings to defaults while preserving user repositories.</summary>
    Task<SettingsDataReplacementResult> ResetAllSettingsAsync(CancellationToken cancellationToken);
}

/// <summary>Describes a committed replacement without exposing repositories to presentation code.</summary>
/// <param name="RequiresRestart">Whether the committed data requires a new process to finish activation.</param>
/// <param name="Warnings">Diagnostic codes for work whose completion could not be confirmed.</param>
public sealed record SettingsDataReplacementResult(bool RequiresRestart, IReadOnlyList<string> Warnings);

/// <summary>Indicates that mutations remain blocked until startup recovers the durable decision.</summary>
public sealed class SettingsDataReplacementRecoveryException : AggregateException
{
    /// <summary>Preserves the transaction and restart-request failures for the page error boundary.</summary>
    public SettingsDataReplacementRecoveryException(bool isCommitted, IEnumerable<Exception> failures)
        : base("Data replacement requires restart recovery before further changes.", failures)
    {
        IsCommitted = isCommitted;
    }

    /// <summary>Gets whether the replacement's durable decision is known to be committed.</summary>
    public bool IsCommitted { get; }
}

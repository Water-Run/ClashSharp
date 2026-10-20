using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Opaque, validated import contents whose scope stays fixed through confirmation.</summary>
/// <remarks>Dispose abandons an unused preview. Only its creating replacement service may consume it once.</remarks>
public interface IPreparedSettingsImport : IDisposable
{
    /// <summary>Gets the scope decoded from the same contents that will be imported.</summary>
    ClashDataPackageScope Scope { get; }
}

/// <summary>Replaces settings through a complete, recoverable data transaction.</summary>
public interface ISettingsDataReplacement
{
    /// <summary>Reads and validates a detached package snapshot without mutating the active data.</summary>
    Task<IPreparedSettingsImport> PrepareImportAsync(string packagePath, CancellationToken cancellationToken);

    /// <summary>Consumes one confirmed snapshot, retaining transaction ownership through runtime publication.</summary>
    Task<SettingsDataReplacementResult> ImportAsync(IPreparedSettingsImport import, CancellationToken cancellationToken);

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

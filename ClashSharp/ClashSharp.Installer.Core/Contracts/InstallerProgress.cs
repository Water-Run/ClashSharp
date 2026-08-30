namespace ClashSharp.Installer.Contracts;

/// <summary>Reports one coarse-grained installer milestone.</summary>
/// <param name="Phase">Current durable or pending phase.</param>
/// <param name="Percent">Monotonic completion percentage from zero through one hundred.</param>
/// <param name="MessageKey">Localization key owned by the WPF presentation layer.</param>
public sealed record InstallerProgress(
    InstallerTransactionPhase Phase,
    int Percent,
    string MessageKey)
{
    /// <summary>Creates and validates a progress value.</summary>
    /// <param name="phase">Current phase.</param>
    /// <param name="percent">Completion percentage.</param>
    /// <param name="messageKey">Localization key.</param>
    /// <returns>A validated progress value.</returns>
    public static InstallerProgress Create(
        InstallerTransactionPhase phase,
        int percent,
        string messageKey)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        return new InstallerProgress(phase, percent, messageKey);
    }
}

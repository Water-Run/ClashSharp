namespace ClashSharp.Installer.Contracts;

/// <summary>Signals that a privileged participant may still be running or have committed unknown state.</summary>
public sealed class InstallerStateUncertainException : Exception
{
    /// <summary>Initializes an uncertain-state failure with a stable diagnostic code.</summary>
    /// <param name="diagnosticCode">Machine-readable recovery reason.</param>
    public InstallerStateUncertainException(string diagnosticCode)
        : base(diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the stable diagnostic code safe to expose in logs and UI.</summary>
    public string DiagnosticCode { get; }
}

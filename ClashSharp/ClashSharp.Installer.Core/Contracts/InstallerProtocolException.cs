namespace ClashSharp.Installer.Contracts;

/// <summary>Represents a stable, non-localized installer protocol failure.</summary>
public sealed class InstallerProtocolException : Exception
{
    /// <summary>Initializes a protocol failure with a stable diagnostic code.</summary>
    /// <param name="diagnosticCode">Machine-readable diagnostic code.</param>
    public InstallerProtocolException(string diagnosticCode)
        : base(diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Initializes a protocol failure while retaining the non-sensitive root exception.</summary>
    /// <param name="diagnosticCode">Machine-readable diagnostic code.</param>
    /// <param name="innerException">Underlying parse or persistence exception.</param>
    public InstallerProtocolException(string diagnosticCode, Exception innerException)
        : base(diagnosticCode, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        ArgumentNullException.ThrowIfNull(innerException);
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the stable diagnostic code safe to expose in logs and UI.</summary>
    public string DiagnosticCode { get; }
}

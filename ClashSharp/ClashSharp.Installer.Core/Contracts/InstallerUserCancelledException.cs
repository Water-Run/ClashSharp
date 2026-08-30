namespace ClashSharp.Installer.Contracts;

/// <summary>Signals an explicit user cancellation outside the caller token, such as UAC denial.</summary>
public sealed class InstallerUserCancelledException : Exception
{
    /// <summary>Creates a user-cancelled result with a stable diagnostic code.</summary>
    public InstallerUserCancelledException(string diagnosticCode)
        : base(diagnosticCode)
    {
        InstallerProtocolValidation.ValidateDiagnosticCode(diagnosticCode);
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the stable non-localized cancellation reason.</summary>
    public string DiagnosticCode { get; }
}

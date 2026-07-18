namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Identifies the control-flow outcome of one startup step or the complete pipeline.</summary>
public enum StartupStepOutcome
{
    /// <summary>Startup may continue without a diagnostic.</summary>
    Succeeded,

    /// <summary>Startup may continue while retaining a non-fatal diagnostic.</summary>
    Warning,

    /// <summary>The current primary launch completed a helper path and must exit.</summary>
    ExitRequested,

    /// <summary>Startup cannot safely continue.</summary>
    Fatal,
}

/// <summary>Returns typed startup control flow with an optional stable diagnostic code.</summary>
/// <param name="Outcome">Startup control-flow outcome.</param>
/// <param name="DiagnosticCode">Stable diagnostic code when available; otherwise null.</param>
public readonly record struct StartupStepResult(StartupStepOutcome Outcome, string? DiagnosticCode)
{
    /// <summary>Creates a successful startup result.</summary>
    public static StartupStepResult Succeeded() => new(StartupStepOutcome.Succeeded, null);

    /// <summary>Creates a non-fatal startup warning.</summary>
    /// <param name="diagnosticCode">Stable warning code.</param>
    public static StartupStepResult Warning(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        return new StartupStepResult(StartupStepOutcome.Warning, diagnosticCode);
    }

    /// <summary>Creates a request to finish the current helper process.</summary>
    public static StartupStepResult ExitRequested() => new(StartupStepOutcome.ExitRequested, null);

    /// <summary>Creates a fatal startup result.</summary>
    /// <param name="diagnosticCode">Stable failure code.</param>
    public static StartupStepResult Fatal(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        return new StartupStepResult(StartupStepOutcome.Fatal, diagnosticCode);
    }
}

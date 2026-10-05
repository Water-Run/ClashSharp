namespace ClashSharp.Installer.Machines;

/// <summary>Records initial recovery evidence before the first public intent or machine mutation.</summary>
public interface IInstallerMachineHelperPreparationEvidence
{
    /// <summary>Captures the first baseline under authenticated exclusion; retries never recapture prepared state.</summary>
    Task CapturePreparationEvidenceAsync(InstallerMachineHelperCommand command, CancellationToken cancellationToken);
}

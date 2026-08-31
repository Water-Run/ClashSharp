using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Contracts;

/// <summary>
/// Trusted platform backend that constructs exact user/release requests outside presentation code.
/// </summary>
public interface IInstallerRuntimeBackend : IDisposable
{
    /// <summary>Captures read-only platform, package, process, release, and recovery facts.</summary>
    Task<InstallerRuntimeInspection> InspectAsync(CancellationToken cancellationToken);

    /// <summary>Executes one operation after constructing its exact target SID and release identity.</summary>
    Task<InstallerExecutionResult> ExecuteAsync(
        InstallerOperation operation,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Trusted read-only facts consumed by the platform-neutral presentation runtime.</summary>
/// <param name="Environment">Platform, installed-package, and exact package-process facts.</param>
/// <param name="DurableTransaction">Protected helper journal, when recovery is pending.</param>
/// <param name="ReleaseVersion">Version bound to the embedded release manifest.</param>
public sealed record InstallerRuntimeInspection(
    InstallerEnvironmentSnapshot Environment,
    InstallerTransactionSnapshot? DurableTransaction,
    string ReleaseVersion)
{
    /// <summary>Validates the complete inspection snapshot without changing machine state.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Environment);
        InstallerProtocolValidation.ValidatePackageVersion(ReleaseVersion);
        if (Environment.InstalledPackageVersion is { } installedVersion)
        {
            InstallerProtocolValidation.ValidatePackageVersion(installedVersion);
        }

        if ((Environment.IsSupported
                ? Environment.BlockingDiagnosticCode is not null
                : !IsDiagnosticCode(Environment.BlockingDiagnosticCode))
            || (Environment.IsApplicationRunning
                && Environment.InstalledPackageVersion is null))
        {
            throw new InstallerProtocolException(
                "installer.runtime.inspection_result_invalid");
        }

        DurableTransaction?.Validate();
        if (DurableTransaction is { } durable
            && !string.Equals(
                durable.Journal.ExpectedPackageVersion,
                ReleaseVersion,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.runtime.inspection_result_invalid");
        }
    }

    private static bool IsDiagnosticCode(string? value) =>
        value is { Length: >= 1 and <= 160 }
        && value.StartsWith("installer.", StringComparison.Ordinal)
        && value.All(static character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_');
}

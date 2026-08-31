using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Platform;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Installer.Windows.Platform;

namespace ClashSharp.Installer.Windows.Execution;

/// <summary>
/// Captures read-only Windows, current-user package, and exact package-process facts for Core
/// preflight authorization.
/// </summary>
public sealed class WindowsInstallerEnvironment : IInstallerEnvironment
{
    private readonly InstallerReleaseManifest _manifest;
    private readonly IInstallerPlatformProbe _platformProbe;
    private readonly IWindowsPackageManagerFacade _packageManager;
    private readonly IWindowsPackageProcessInspector _processInspector;
    private readonly Func<string?> _currentSid;

    /// <summary>Creates the native read-only environment adapter for one embedded manifest.</summary>
    /// <param name="manifest">The strictly parsed manifest embedded in the Installer executable.</param>
    public WindowsInstallerEnvironment(InstallerReleaseManifest manifest)
        : this(
            manifest,
            new WindowsInstallerPlatformProbe(),
            new WindowsPackageManagerFacade(),
            new WindowsPackageProcessInspector(),
            WindowsInstallerCurrentUser.GetSid)
    {
    }

    internal WindowsInstallerEnvironment(
        InstallerReleaseManifest manifest,
        IInstallerPlatformProbe platformProbe,
        IWindowsPackageManagerFacade packageManager,
        IWindowsPackageProcessInspector processInspector,
        Func<string?> currentSid)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(platformProbe);
        ArgumentNullException.ThrowIfNull(packageManager);
        ArgumentNullException.ThrowIfNull(processInspector);
        ArgumentNullException.ThrowIfNull(currentSid);
        manifest.Validate();
        _manifest = manifest;
        _platformProbe = platformProbe;
        _packageManager = packageManager;
        _processInspector = processInspector;
        _currentSid = currentSid;
    }

    /// <inheritdoc />
    public Task<InstallerEnvironmentSnapshot> InspectAsync(
        InstallerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReleaseIdentity(request);

        string currentSid;
        try
        {
            currentSid = _currentSid()
                ?? throw new InstallerProtocolException(
                    "installer.environment.current_user_invalid");
            InstallerProtocolValidation.ValidateTargetSid(currentSid);
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.environment.current_user_invalid",
                exception);
        }

        if (!string.Equals(currentSid, request.TargetSid, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.environment.target_user_mismatch");
        }

        InstallerPlatformFacts platformFacts = _platformProbe.Inspect(cancellationToken);
        InstallerPlatformAssessment platform = InstallerPlatformPolicy.Evaluate(platformFacts);
        if (!platformFacts.IsWindows)
        {
            return Task.FromResult(new InstallerEnvironmentSnapshot(
                IsSupported: false,
                InstalledPackageVersion: null,
                IsApplicationRunning: false,
                BlockingDiagnosticCode: platform.DiagnosticCode));
        }

        InstallerInstalledPackage? installed;
        try
        {
            installed = WindowsPackageRegistrationInspector.Inspect(
                _packageManager,
                userSecurityId: string.Empty,
                _manifest);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.package.inspection_failed",
                exception);
        }

        bool isApplicationRunning = installed is not null
            && _processInspector.IsApplicationRunning(_manifest, cancellationToken);
        return Task.FromResult(new InstallerEnvironmentSnapshot(
            platform.IsSupported,
            installed?.Version,
            isApplicationRunning,
            platform.IsSupported ? null : platform.DiagnosticCode));
    }

    private void ValidateReleaseIdentity(InstallerRequest request)
    {
        if (!string.Equals(
                request.ExpectedPackageVersion,
                _manifest.ExpectedPackageVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                request.InstallerPayloadSha256,
                _manifest.InstallerPayloadSha256,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.release.identity_mismatch");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Packages;

/// <summary>Maps one exact user/family query through the shared fail-closed identity policy.</summary>
internal static class WindowsPackageRegistrationInspector
{
    internal static InstallerInstalledPackage? Inspect(
        IWindowsPackageManagerFacade packageManager,
        string userSecurityId,
        InstallerReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(packageManager);
        ArgumentNullException.ThrowIfNull(userSecurityId);
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        if (userSecurityId.Length != 0)
        {
            InstallerProtocolValidation.ValidateTargetSid(userSecurityId);
        }

        IReadOnlyList<WindowsPackageRegistration> registrations =
            packageManager.FindPackagesForUser(
                userSecurityId,
                manifest.PackageIdentity.PackageFamilyName);
        if (registrations is null)
        {
            throw new InstallerProtocolException(
                "installer.package.inspection_result_invalid");
        }

        if (registrations.Count == 0)
        {
            return null;
        }

        if (registrations.Count != 1)
        {
            throw new InstallerProtocolException(
                "installer.package.registration_ambiguous");
        }

        WindowsPackageRegistration registration = registrations[0]
            ?? throw new InstallerProtocolException(
                "installer.package.inspection_result_invalid");
        if (registration.IsBundle
            || registration.IsDevelopmentMode
            || registration.IsFramework
            || registration.IsOptional
            || registration.IsResourcePackage
            || registration.IsStub)
        {
            throw new InstallerProtocolException(
                "installer.package.installed_identity_mismatch");
        }

        var installed = new InstallerInstalledPackage(
            registration.Name,
            registration.Publisher,
            registration.PublisherId,
            registration.Version,
            registration.Architecture,
            registration.ResourceId,
            registration.PackageFullName,
            registration.PackageFamilyName,
            registration.IsHealthy);
        if (!installed.MatchesReleaseFamily(manifest))
        {
            throw new InstallerProtocolException(
                "installer.package.installed_identity_mismatch");
        }

        return installed;
    }
}

/// <summary>
/// Independently proves the exact target-user AppXSVC result before the helper commits PackageCommitted.
/// </summary>
internal sealed class WindowsTargetUserPackageCommitInspector
{
    private readonly IWindowsPackageManagerFacade _packageManager;

    internal WindowsTargetUserPackageCommitInspector(
        IWindowsPackageManagerFacade packageManager)
    {
        ArgumentNullException.ThrowIfNull(packageManager);
        _packageManager = packageManager;
    }

    internal void Verify(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        request.Validate();
        manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            throw new InstallerProtocolException("installer.package.platform_unsupported");
        }

        if (!string.Equals(
                request.ExpectedPackageVersion,
                manifest.ExpectedPackageVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                request.InstallerPayloadSha256,
                manifest.InstallerPayloadSha256,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.release.identity_mismatch");
        }

        InstallerInstalledPackage? installed;
        try
        {
            installed = WindowsPackageRegistrationInspector.Inspect(
                _packageManager,
                request.TargetSid,
                manifest);
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

        bool satisfied = request.Operation switch
        {
            InstallerOperation.Install or InstallerOperation.Repair =>
                installed is { IsHealthy: true }
                && string.Equals(
                    installed.Version,
                    request.ExpectedPackageVersion,
                    StringComparison.Ordinal),
            InstallerOperation.Uninstall => installed is null,
            _ => false,
        };
        if (!satisfied)
        {
            throw new InstallerProtocolException(
                request.Operation == InstallerOperation.Uninstall
                    ? "installer.package.removal_verification_failed"
                    : "installer.package.deployment_verification_failed");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

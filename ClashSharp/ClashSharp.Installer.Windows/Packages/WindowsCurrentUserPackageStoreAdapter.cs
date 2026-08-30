using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Packages;

/// <summary>
/// Applies exact MSIX registration changes only to the invoking Windows 11 user.
/// </summary>
public sealed class WindowsCurrentUserPackageStoreAdapter : IInstallerPackageStoreAdapter
{
    private readonly IWindowsPackageManagerFacade _packageManager;
    private readonly Func<string?> _currentSid;

    /// <summary>Initializes the adapter over the Windows deployment service.</summary>
    public WindowsCurrentUserPackageStoreAdapter()
        : this(new WindowsPackageManagerFacade(), GetCurrentSid)
    {
    }

    internal WindowsCurrentUserPackageStoreAdapter(
        IWindowsPackageManagerFacade packageManager,
        Func<string?> currentSid)
    {
        ArgumentNullException.ThrowIfNull(packageManager);
        ArgumentNullException.ThrowIfNull(currentSid);
        _packageManager = packageManager;
        _currentSid = currentSid;
    }

    /// <inheritdoc />
    public Task<InstallerInstalledPackage?> InspectAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        try
        {
            InstallerInstalledPackage? installed =
                WindowsPackageRegistrationInspector.Inspect(
                    _packageManager,
                    string.Empty,
                    release.Manifest);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<InstallerInstalledPackage?>(installed);
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
    }

    /// <inheritdoc />
    public async Task DeployAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        WindowsInstallerReleaseLease windowsLease = ValidateBoundary(
            request,
            release,
            cancellationToken);
        if (request.Operation is not (InstallerOperation.Install or InstallerOperation.Repair))
        {
            throw new InstallerProtocolException("installer.package.operation_invalid");
        }

        WindowsLockedPayloadFile primary = windowsLease.RequireFile(
            release.Manifest.Files.Single(static file =>
                file.Role == InstallerPayloadFileRole.PrimaryPackage));
        Dictionary<string, InstallerPayloadFileEntry> filesByPath = release.Manifest.Files
            .ToDictionary(static file => file.Path, StringComparer.Ordinal);
        Uri[] dependencies = release.Manifest.Dependencies
            .Select(dependency => filesByPath.TryGetValue(
                    dependency.Path,
                    out InstallerPayloadFileEntry? entry)
                ? LocalFileUri(windowsLease.RequireFile(entry))
                : throw new InstallerProtocolException(
                    "installer.release.locked_file_set_invalid"))
            .ToArray();
        var deployment = new WindowsPackageDeploymentRequest(
            LocalFileUri(primary),
            dependencies,
            AllowUnsigned: false,
            DeferRegistrationWhenPackagesAreInUse: false,
            DeveloperMode: false,
            ForceAppShutdown: false,
            ForceTargetAppShutdown: false,
            ForceUpdateFromAnyVersion: false,
            InstallAllResources: false,
            RequiredContentGroupOnly: false,
            RetainFilesOnFailure: false,
            StageInPlace: false);

        await windowsLease.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // PackageManager cannot reliably cancel an operation already handed to AppXSVC.
            // Await its terminal state so the caller cannot release the file lease too early.
            await _packageManager.DeployAsync(deployment).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.package.deployment_failed",
                exception);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerInstalledPackage installedPackage,
        CancellationToken cancellationToken)
    {
        WindowsInstallerReleaseLease windowsLease = ValidateBoundary(
            request,
            release,
            cancellationToken);
        ArgumentNullException.ThrowIfNull(installedPackage);
        if (request.Operation != InstallerOperation.Uninstall)
        {
            throw new InstallerProtocolException("installer.package.operation_invalid");
        }

        if (!installedPackage.MatchesReleaseFamily(release.Manifest))
        {
            throw new InstallerProtocolException(
                "installer.package.installed_identity_mismatch");
        }

        await windowsLease.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Removal has the same AppXSVC cancellation semantics as deployment.
            await _packageManager
                .RemoveAsync(installedPackage.PackageFullName)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.package.removal_failed",
                exception);
        }
    }

    private WindowsInstallerReleaseLease ValidateBoundary(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Release.Validate();
        release.Manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            throw new InstallerProtocolException("installer.package.platform_unsupported");
        }

        if (release is not WindowsInstallerReleaseLease windowsLease
            || !release.Manifest.Matches(release.Release))
        {
            throw new InstallerProtocolException("installer.release.windows_lease_required");
        }

        windowsLease.RequireRequest(request);
        if (!string.Equals(_currentSid(), request.TargetSid, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.package.target_user_mismatch");
        }

        return windowsLease;
    }

    private static Uri LocalFileUri(WindowsLockedPayloadFile file)
    {
        var uri = new Uri(file.FullPath, UriKind.Absolute);
        if (!uri.IsFile)
        {
            throw new InstallerProtocolException("installer.package.payload_uri_invalid");
        }

        return uri;
    }

    private static string? GetCurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

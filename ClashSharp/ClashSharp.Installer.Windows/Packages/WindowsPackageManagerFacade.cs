using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.System;

namespace ClashSharp.Installer.Windows.Packages;

internal sealed class WindowsPackageManagerFacade : IWindowsPackageManagerFacade
{
    private readonly PackageManager _packageManager = new();

    public IReadOnlyList<WindowsPackageRegistration> FindPackagesForUser(
        string userSecurityId,
        string packageFamilyName) =>
        _packageManager
            .FindPackagesForUser(userSecurityId, packageFamilyName)
            .Select(MapRegistration)
            .ToArray();

    public async Task DeployAsync(WindowsPackageDeploymentRequest request)
    {
        AddPackageOptions options = CreateOptions(request);
        _ = await _packageManager
            .AddPackageByUriAsync(request.PrimaryPackageUri, options)
            .AsTask()
            .ConfigureAwait(false);
    }

    public async Task RemoveAsync(string packageFullName)
    {
        _ = await _packageManager
            .RemovePackageAsync(packageFullName)
            .AsTask()
            .ConfigureAwait(false);
    }

    internal static AddPackageOptions CreateOptions(WindowsPackageDeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = new AddPackageOptions
        {
            AllowUnsigned = request.AllowUnsigned,
            DeferRegistrationWhenPackagesAreInUse =
                request.DeferRegistrationWhenPackagesAreInUse,
            DeveloperMode = request.DeveloperMode,
            ForceAppShutdown = request.ForceAppShutdown,
            ForceTargetAppShutdown = request.ForceTargetAppShutdown,
            ForceUpdateFromAnyVersion = request.ForceUpdateFromAnyVersion,
            InstallAllResources = request.InstallAllResources,
            RequiredContentGroupOnly = request.RequiredContentGroupOnly,
            RetainFilesOnFailure = request.RetainFilesOnFailure,
            StageInPlace = request.StageInPlace,
        };
        foreach (Uri dependencyPackageUri in request.DependencyPackageUris)
        {
            options.DependencyPackageUris.Add(dependencyPackageUri);
        }

        return options;
    }

    private static WindowsPackageRegistration MapRegistration(Package package)
    {
        PackageId id = package.Id;
        PackageVersion version = id.Version;
        return new WindowsPackageRegistration(
            id.Name,
            id.Publisher,
            id.PublisherId,
            $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
            Architecture(id.Architecture),
            id.ResourceId ?? string.Empty,
            id.FullName,
            id.FamilyName,
            IsHealthy(package.Status),
            package.IsBundle,
            package.IsDevelopmentMode,
            package.IsFramework,
            package.IsOptional,
            package.IsResourcePackage,
            package.IsStub);
    }

    private static string Architecture(ProcessorArchitecture architecture) => architecture switch
    {
        ProcessorArchitecture.X64 => "x64",
        ProcessorArchitecture.X86 => "x86",
        ProcessorArchitecture.Arm => "arm",
        ProcessorArchitecture.Arm64 => "arm64",
        ProcessorArchitecture.Neutral => "neutral",
        _ => "unknown",
    };

    private static bool IsHealthy(global::Windows.ApplicationModel.PackageStatus status) =>
        !status.DataOffline
        && !status.DependencyIssue
        && !status.DeploymentInProgress
        && !status.Disabled
        && !status.IsPartiallyStaged
        && !status.LicenseIssue
        && !status.Modified
        && !status.NeedsRemediation
        && !status.NotAvailable
        && !status.PackageOffline
        && !status.Servicing
        && !status.Tampered;
}

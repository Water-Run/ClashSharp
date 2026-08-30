namespace ClashSharp.Installer.Windows.Packages;

internal interface IWindowsPackageManagerFacade
{
    IReadOnlyList<WindowsPackageRegistration> FindPackagesForUser(
        string userSecurityId,
        string packageFamilyName);

    Task DeployAsync(WindowsPackageDeploymentRequest request);

    Task RemoveAsync(string packageFullName);
}

internal sealed record WindowsPackageRegistration(
    string Name,
    string Publisher,
    string PublisherId,
    string Version,
    string Architecture,
    string ResourceId,
    string PackageFullName,
    string PackageFamilyName,
    bool IsHealthy,
    bool IsBundle,
    bool IsDevelopmentMode,
    bool IsFramework,
    bool IsOptional,
    bool IsResourcePackage,
    bool IsStub);

internal sealed record WindowsPackageDeploymentRequest(
    Uri PrimaryPackageUri,
    IReadOnlyList<Uri> DependencyPackageUris,
    bool AllowUnsigned,
    bool DeferRegistrationWhenPackagesAreInUse,
    bool DeveloperMode,
    bool ForceAppShutdown,
    bool ForceTargetAppShutdown,
    bool ForceUpdateFromAnyVersion,
    bool InstallAllResources,
    bool RequiredContentGroupOnly,
    bool RetainFilesOnFailure,
    bool StageInPlace);

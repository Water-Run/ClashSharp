using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperMachineBackend
{
    string ResolveTargetProfile(string targetSid, CancellationToken cancellationToken);

    string CreateAuthenticationToken();

    WindowsMachineDeploymentPlan CreatePlan(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association,
        string targetProfileRoot,
        bool removalPlan);

    /// <summary>
    /// Builds a fixed-root removal plan whose profile-derived fields must never authorize service
    /// mutation. Callers must first prove the fixed service is absent.
    /// </summary>
    WindowsMachineDeploymentPlan CreateProfileIndependentRemovalPlan(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association);

    IWindowsMachineRootGuard CreateRootGuard(
        WindowsMachineDeploymentPlan plan,
        bool createMissing);

    IWindowsMachineAssociationStore CreateAssociationStore(
        WindowsMachineDeploymentPlan plan,
        IWindowsMachineRootGuard rootGuard);

    bool ServiceExists(CancellationToken cancellationToken);

    bool PayloadResidueExists(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    Task StopDisableAndFenceServiceAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    Task ConfigureStartServiceAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    Task StopDeleteServiceAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    void VerifyServicePrepared(CancellationToken cancellationToken);

    void VerifyServiceInstalled(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    void VerifyServiceAbsent(CancellationToken cancellationToken);

    Task StagePayloadAsync(
        WindowsMachineDeploymentPlan plan,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);

    void PromotePayload(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    void RemovePayload(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    void VerifyPayloadInstalled(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    void VerifyPayloadAbsent(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    void RemoveEmptyRoots(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    void VerifyRootsAbsent(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);

    /// <summary>
    /// Proves that the fixed service and both fixed machine roots are absent without deriving any
    /// target-profile path. This capability must remain read-only.
    /// </summary>
    void VerifyProfileIndependentRemovalPostcondition(
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapts the independently tested Windows roots, payload slots, SCM, association, and profile
/// primitives to the helper's transaction-aware orchestration layer.
/// </summary>
internal sealed class WindowsMachineHelperMachineBackend
    : IWindowsMachineHelperMachineBackend
{
    private readonly WindowsMachineDeploymentRoots _roots;
    private readonly WindowsTargetProfileResolver _profileResolver;
    private readonly WindowsMachinePayloadTreeVerifier _payloadVerifier;
    private readonly WindowsMachinePayloadMutation _payloadMutation;
    private readonly WindowsServiceConfigurationVerifier _serviceVerifier;
    private readonly WindowsServiceMutation _serviceMutation;
    private readonly WindowsMachineRootCleanup _rootCleanup;

    internal WindowsMachineHelperMachineBackend()
        : this(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolderOption.DoNotVerify),
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolderOption.DoNotVerify))
    {
    }

    internal WindowsMachineHelperMachineBackend(
        string programFilesRoot,
        string commonApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programFilesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonApplicationDataRoot);
        _roots = WindowsMachineDeploymentRoots.Create(
            programFilesRoot,
            commonApplicationDataRoot);
        _profileResolver = new WindowsTargetProfileResolver();
        _payloadVerifier = new WindowsMachinePayloadTreeVerifier();
        _payloadMutation = new WindowsMachinePayloadMutation(
            WindowsMachinePayloadSlotNative.Instance,
            _payloadVerifier);
        _serviceVerifier = new WindowsServiceConfigurationVerifier();
        _serviceMutation = new WindowsServiceMutation();
        _rootCleanup = new WindowsMachineRootCleanup();
    }

    public string ResolveTargetProfile(
        string targetSid,
        CancellationToken cancellationToken) =>
        _profileResolver.Resolve(targetSid, cancellationToken);

    public string CreateAuthenticationToken() =>
        InstallerMachineAssociation.GenerateAuthenticationToken();

    public WindowsMachineDeploymentPlan CreatePlan(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association,
        string targetProfileRoot,
        bool removalPlan) =>
        removalPlan
            ? WindowsMachineDeploymentPlan.CreateForRemoval(
                    request,
                    manifest,
                    association,
                    _roots.ProgramFilesRoot,
                    _roots.CommonApplicationDataRoot,
                    targetProfileRoot)
            : WindowsMachineDeploymentPlan.Create(
                    request,
                    manifest,
                    association,
                    _roots.ProgramFilesRoot,
                    _roots.CommonApplicationDataRoot,
                    targetProfileRoot);

    public WindowsMachineDeploymentPlan CreateProfileIndependentRemovalPlan(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association)
    {
        string? volumeRoot = Path.GetPathRoot(_roots.ProgramFilesRoot);
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new InstallerProtocolException(
                "installer.machine.root_identity_invalid");
        }

        string unavailableProfile = Path.GetFullPath(Path.Combine(
            volumeRoot,
            "ClashSharp.UnavailableTargetProfile"));
        return WindowsMachineDeploymentPlan.CreateForRemoval(
            request,
            manifest,
            association,
            _roots.ProgramFilesRoot,
            _roots.CommonApplicationDataRoot,
            unavailableProfile);
    }

    public IWindowsMachineRootGuard CreateRootGuard(
        WindowsMachineDeploymentPlan plan,
        bool createMissing) =>
        createMissing
            ? WindowsMachineRootGuard.CreateDefault(plan)
            : WindowsMachineRootGuard.CreateReadOnlyDefault(plan);

    public IWindowsMachineAssociationStore CreateAssociationStore(
        WindowsMachineDeploymentPlan plan,
        IWindowsMachineRootGuard rootGuard) =>
        new WindowsMachineAssociationStore(
            plan,
            rootGuard,
            WindowsMachineAssociationFileNative.Instance);

    public bool ServiceExists(CancellationToken cancellationToken) =>
        _serviceVerifier.InspectOptional(cancellationToken) is not null;

    public bool PayloadResidueExists(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        SlotStatuses(plan, cancellationToken).Any(static status =>
            status != WindowsMachinePayloadTreeStatus.Missing);

    public Task StopDisableAndFenceServiceAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _serviceMutation.StopDisableAndFenceAsync(plan, cancellationToken);

    public Task ConfigureStartServiceAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _serviceMutation.ConfigureStartAndVerifyAsync(plan, cancellationToken);

    public Task StopDeleteServiceAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _serviceMutation.StopDeleteAndVerifyAsync(plan, cancellationToken);

    public void VerifyServicePrepared(CancellationToken cancellationToken) =>
        _serviceVerifier.VerifyPrepared(cancellationToken);

    public void VerifyServiceInstalled(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _serviceVerifier.VerifyInstalled(plan, requireRunning: true, cancellationToken);

    public void VerifyServiceAbsent(CancellationToken cancellationToken) =>
        _serviceVerifier.VerifyAbsent(cancellationToken);

    public Task StagePayloadAsync(
        WindowsMachineDeploymentPlan plan,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release is not WindowsInstallerReleaseLease windowsRelease)
        {
            throw new InstallerProtocolException(
                "installer.release.windows_lease_required");
        }

        return _payloadMutation.StageAsync(plan, windowsRelease, cancellationToken);
    }

    public void PromotePayload(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _payloadMutation.PromoteAndVerify(plan, cancellationToken);

    public void RemovePayload(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _payloadMutation.RemoveAndVerify(plan, cancellationToken);

    public void VerifyPayloadInstalled(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _payloadMutation.VerifyInstalled(plan, cancellationToken);

    public void VerifyPayloadAbsent(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        if (SlotStatuses(plan, cancellationToken).Any(static status =>
                status != WindowsMachinePayloadTreeStatus.Missing))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_removal_verification_failed");
        }
    }

    public void RemoveEmptyRoots(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _rootCleanup.RemoveAndVerify(plan, cancellationToken);

    public void VerifyRootsAbsent(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken) =>
        _rootCleanup.VerifyAbsent(plan, cancellationToken);

    public void VerifyProfileIndependentRemovalPostcondition(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _serviceVerifier.VerifyAbsent(cancellationToken);
        _rootCleanup.VerifyAbsent(_roots, cancellationToken);
    }

    private IEnumerable<WindowsMachinePayloadTreeStatus> SlotStatuses(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        foreach (string root in new[]
                 {
                     plan.CurrentRoot,
                     plan.StagingRoot,
                     plan.PreviousRoot,
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return _payloadVerifier.Inspect(plan, root, cancellationToken);
        }
    }
}

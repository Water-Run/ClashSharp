using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Composes the fixed Windows machine primitives under the helper's durable command disposition.
/// Committed replay paths are observation-only; executable paths preserve association evidence until
/// the owned service and every payload slot have reached their independently verified postcondition.
/// </summary>
internal sealed class WindowsMachineHelperMachineOperations
    : IWindowsMachineHelperMachineOperations
{
    private const string TargetProfileMissingDiagnosticCode =
        "installer.machine.target_profile_missing";

    private readonly IWindowsMachineHelperMachineBackend _backend;

    internal WindowsMachineHelperMachineOperations()
        : this(new WindowsMachineHelperMachineBackend())
    {
    }

    internal WindowsMachineHelperMachineOperations(
        IWindowsMachineHelperMachineBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    public async Task PrepareAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, disposition, cancellationToken);
        if (request.Operation is InstallerOperation.Install or InstallerOperation.Repair)
        {
            if (disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay)
            {
                await VerifyProvisionReservedAsync(request, release, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            using ObservedContext context = await OpenProvisionContextAsync(
                    request,
                    release.Manifest,
                    createMissingRoots: true,
                    cancellationToken)
                .ConfigureAwait(false);
            await _backend.StopDisableAndFenceServiceAsync(
                    context.Plan,
                    cancellationToken)
                .ConfigureAwait(false);
            await context.AssociationStore.WriteAndVerifyAsync(
                    context.Plan.Association,
                    cancellationToken)
                .ConfigureAwait(false);
            _backend.VerifyServicePrepared(cancellationToken);
            await context.AssociationStore.VerifyExactAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Operation != InstallerOperation.Uninstall)
        {
            throw new InstallerProtocolException(
                "installer.machine.operation_invalid");
        }

        if (disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay)
        {
            await VerifyRemovalAuthorizedAsync(request, release, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ObservedContext? removalContext =
            await OpenRemovalContextOrVerifyAlreadyRemovedAsync(
                    request,
                    release.Manifest,
                    createMissingRoots: true,
                    cancellationToken)
                .ConfigureAwait(false);
        if (removalContext is null)
        {
            return;
        }

        using (removalContext)
        {
            if (removalContext.HasExactAssociation)
            {
                if (removalContext.IsProfileIndependent)
                {
                    _backend.VerifyServiceAbsent(cancellationToken);
                }
                else
                {
                    await _backend.StopDisableAndFenceServiceAsync(
                            removalContext.Plan,
                            cancellationToken)
                        .ConfigureAwait(false);
                    _backend.VerifyServicePrepared(cancellationToken);
                }

                await removalContext.AssociationStore.VerifyExactAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _backend.VerifyServiceAbsent(cancellationToken);
                _backend.VerifyPayloadAbsent(removalContext.Plan, cancellationToken);
                await removalContext.AssociationStore.VerifyAbsentAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, disposition, cancellationToken);
        if (request.Operation is not (InstallerOperation.Install or InstallerOperation.Repair))
        {
            throw new InstallerProtocolException(
                "installer.machine.apply_operation_invalid");
        }

        if (disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay)
        {
            await VerifyInstalledAsync(request, release, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using ObservedContext context = await OpenExactAssociationContextAsync(
                request,
                release.Manifest,
                removalPlan: false,
                createMissingRoots: true,
                cancellationToken)
            .ConfigureAwait(false);
        await _backend.StagePayloadAsync(context.Plan, release, cancellationToken)
            .ConfigureAwait(false);
        await _backend.StopDisableAndFenceServiceAsync(context.Plan, cancellationToken)
            .ConfigureAwait(false);
        _backend.PromotePayload(context.Plan, cancellationToken);
        await _backend.ConfigureStartServiceAsync(context.Plan, cancellationToken)
            .ConfigureAwait(false);
        await VerifyInstalledContextAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, disposition, cancellationToken);
        if (request.Operation != InstallerOperation.Uninstall)
        {
            throw new InstallerProtocolException(
                "installer.machine.remove_operation_invalid");
        }

        if (disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay)
        {
            VerifyRemovedOrProfileIndependent(
                request,
                release.Manifest,
                cancellationToken);
            return;
        }

        ObservedContext? context = await OpenRemovalContextOrVerifyAlreadyRemovedAsync(
                request,
                release.Manifest,
                createMissingRoots: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            return;
        }

        WindowsMachineDeploymentPlan plan = context.Plan;
        try
        {
            if (context.IsProfileIndependent)
            {
                _backend.VerifyServiceAbsent(cancellationToken);
            }
            else
            {
                await _backend.StopDeleteServiceAsync(plan, cancellationToken)
                    .ConfigureAwait(false);
            }

            _backend.RemovePayload(plan, cancellationToken);
            await context.AssociationStore.DeleteAndVerifyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            context.Dispose();
        }

        _backend.RemoveEmptyRoots(plan, cancellationToken);
        VerifyRemoved(plan, cancellationToken);
    }

    public async Task VerifyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, disposition: null, cancellationToken);
        if (request.Operation is InstallerOperation.Install or InstallerOperation.Repair)
        {
            await VerifyInstalledAsync(request, release, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Operation == InstallerOperation.Uninstall)
        {
            VerifyRemovedOrProfileIndependent(
                request,
                release.Manifest,
                cancellationToken);
            return;
        }

        throw new InstallerProtocolException(
            "installer.machine.operation_invalid");
    }

    private async Task VerifyProvisionReservedAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        using ObservedContext context = await OpenExactAssociationContextAsync(
                request,
                release.Manifest,
                removalPlan: false,
                createMissingRoots: false,
                cancellationToken)
            .ConfigureAwait(false);
        _backend.VerifyServicePrepared(cancellationToken);
        await context.AssociationStore.VerifyExactAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task VerifyRemovalAuthorizedAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        InstallerProtocolException? authorizationFailure = null;
        try
        {
            using ObservedContext context = await OpenRemovalContextAsync(
                    request,
                    release.Manifest,
                    createMissingRoots: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (context.HasExactAssociation)
            {
                _backend.VerifyServicePrepared(cancellationToken);
                await context.AssociationStore.VerifyExactAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _backend.VerifyServiceAbsent(cancellationToken);
                _backend.VerifyPayloadAbsent(context.Plan, cancellationToken);
                await context.AssociationStore.VerifyAbsentAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }
        catch (InstallerProtocolException exception) when (
            IsTargetProfileMissing(exception))
        {
            try
            {
                _backend.VerifyProfileIndependentRemovalPostcondition(cancellationToken);
                return;
            }
            catch (InstallerProtocolException removedFailure)
            {
                try
                {
                    using ObservedContext context =
                        await OpenProfileIndependentRemovalContextAsync(
                                request,
                                release.Manifest,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (!context.HasExactAssociation)
                    {
                        Rethrow(removedFailure);
                    }

                    _backend.VerifyServiceAbsent(cancellationToken);
                    await context.AssociationStore
                        .VerifyExactAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (InstallerProtocolException profileIndependentFailure) when (
                    string.Equals(
                        profileIndependentFailure.DiagnosticCode,
                        "installer.machine.removal_not_authorized",
                        StringComparison.Ordinal))
                {
                    Rethrow(removedFailure);
                }
            }
        }
        catch (InstallerProtocolException exception)
        {
            authorizationFailure = exception;
        }

        WindowsMachineDeploymentPlan absentPlan = CreateCandidatePlan(
            request,
            release.Manifest,
            removalPlan: true,
            cancellationToken);
        try
        {
            VerifyRemoved(absentPlan, cancellationToken);
        }
        catch (InstallerProtocolException)
        {
            throw authorizationFailure;
        }
    }

    private async Task VerifyInstalledAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        using ObservedContext context = await OpenExactAssociationContextAsync(
                request,
                release.Manifest,
                removalPlan: false,
                createMissingRoots: false,
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyInstalledContextAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyInstalledContextAsync(
        ObservedContext context,
        CancellationToken cancellationToken)
    {
        await context.AssociationStore.VerifyExactAsync(cancellationToken)
            .ConfigureAwait(false);
        _backend.VerifyPayloadInstalled(context.Plan, cancellationToken);
        _backend.VerifyServiceInstalled(context.Plan, cancellationToken);
    }

    private void VerifyRemoved(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        _backend.VerifyServiceAbsent(cancellationToken);
        _backend.VerifyPayloadAbsent(plan, cancellationToken);
        _backend.VerifyRootsAbsent(plan, cancellationToken);
    }

    private async Task<ObservedContext> OpenProvisionContextAsync(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        bool createMissingRoots,
        CancellationToken cancellationToken)
    {
        ObservedContext context = await OpenObservedContextAsync(
                request,
                manifest,
                removalPlan: false,
                createMissingRoots,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            bool serviceExists = _backend.ServiceExists(cancellationToken);
            bool payloadResidue = _backend.PayloadResidueExists(
                context.Plan,
                cancellationToken);
            InstallerMachineProvisionDecision decision =
                InstallerMachineOwnershipPolicy.DecideProvision(
                    request,
                    context.Observation,
                    serviceExists,
                    payloadResidue,
                    context.Plan.Association.AuthenticationToken);
            if (decision.Disposition
                != InstallerMachineProvisionDisposition.Provision)
            {
                throw new InstallerProtocolException(
                    "installer.machine.reassociation_required");
            }

            InstallerMachineAssociation selected = InstallerMachineAssociation.Create(
                request.TargetSid,
                decision.AuthenticationToken!);
            await RebindAsync(context, selected, cancellationToken).ConfigureAwait(false);
            context.HasExactAssociation = context.Observation.Association == selected;
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private async Task<ObservedContext> OpenExactAssociationContextAsync(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        bool removalPlan,
        bool createMissingRoots,
        CancellationToken cancellationToken)
    {
        ObservedContext context = await OpenObservedContextAsync(
                request,
                manifest,
                removalPlan,
                createMissingRoots,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            InstallerMachineAssociation association = context.Observation.Association
                is { } observed
                && string.Equals(
                    observed.OwnerSid,
                    request.TargetSid,
                    StringComparison.Ordinal)
                    ? observed
                    : throw new InstallerProtocolException(
                        "installer.machine.association_not_exact");
            await RebindAsync(context, association, cancellationToken).ConfigureAwait(false);
            context.HasExactAssociation = true;
            await context.AssociationStore.VerifyExactAsync(cancellationToken)
                .ConfigureAwait(false);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private async Task<ObservedContext> OpenRemovalContextAsync(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        bool createMissingRoots,
        CancellationToken cancellationToken)
    {
        ObservedContext context = await OpenObservedContextAsync(
                request,
                manifest,
                removalPlan: true,
                createMissingRoots,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (InstallerMachineOwnershipPolicy.MayRemove(
                    request.TargetSid,
                    context.Observation))
            {
                InstallerMachineAssociation association = context.Observation.Association!;
                await RebindAsync(context, association, cancellationToken).ConfigureAwait(false);
                context.HasExactAssociation = true;
                return context;
            }

            bool serviceExists = _backend.ServiceExists(cancellationToken);
            bool payloadResidue = _backend.PayloadResidueExists(
                context.Plan,
                cancellationToken);
            if (context.Observation.Status != InstallerMachineAssociationStatus.Missing
                || serviceExists
                || payloadResidue)
            {
                throw new InstallerProtocolException(
                    "installer.machine.removal_not_authorized");
            }

            context.HasExactAssociation = false;
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private async Task<ObservedContext?> OpenRemovalContextOrVerifyAlreadyRemovedAsync(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        bool createMissingRoots,
        CancellationToken cancellationToken)
    {
        try
        {
            return await OpenRemovalContextAsync(
                    request,
                    manifest,
                    createMissingRoots,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InstallerProtocolException exception) when (
            IsTargetProfileMissing(exception))
        {
            try
            {
                _backend.VerifyProfileIndependentRemovalPostcondition(cancellationToken);
                return null;
            }
            catch (InstallerProtocolException removedFailure)
            {
                try
                {
                    return await OpenProfileIndependentRemovalContextAsync(
                            request,
                            manifest,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InstallerProtocolException profileIndependentFailure) when (
                    string.Equals(
                        profileIndependentFailure.DiagnosticCode,
                        "installer.machine.removal_not_authorized",
                        StringComparison.Ordinal))
                {
                    return Rethrow<ObservedContext?>(removedFailure);
                }
            }
        }
    }

    private async Task<ObservedContext> OpenProfileIndependentRemovalContextAsync(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        InstallerMachineAssociation candidate = InstallerMachineAssociation.Create(
            request.TargetSid,
            _backend.CreateAuthenticationToken());
        WindowsMachineDeploymentPlan plan =
            _backend.CreateProfileIndependentRemovalPlan(
                request,
                manifest,
                candidate);
        IWindowsMachineRootGuard? rootGuard = null;
        IWindowsMachineAssociationStore? associationStore = null;
        try
        {
            rootGuard = _backend.CreateRootGuard(plan, createMissing: false);
            await rootGuard.EnsureProtectedAsync(plan, cancellationToken)
                .ConfigureAwait(false);
            associationStore = _backend.CreateAssociationStore(plan, rootGuard);
            InstallerMachineAssociationObservation observation =
                await associationStore.InspectAsync(cancellationToken)
                    .ConfigureAwait(false);
            observation.Validate();
            _backend.VerifyServiceAbsent(cancellationToken);
            var context = new ObservedContext(
                plan,
                rootGuard,
                associationStore,
                observation,
                hasExactAssociation: false,
                isProfileIndependent: true);
            rootGuard = null;
            associationStore = null;
            try
            {
                if (InstallerMachineOwnershipPolicy.MayRemove(
                        request.TargetSid,
                        observation))
                {
                    InstallerMachineAssociation association = observation.Association!;
                    await RebindAsync(context, association, cancellationToken)
                        .ConfigureAwait(false);
                    context.HasExactAssociation = true;
                    return context;
                }

                bool payloadResidue = _backend.PayloadResidueExists(
                    context.Plan,
                    cancellationToken);
                if (observation.Status != InstallerMachineAssociationStatus.Missing
                    || payloadResidue)
                {
                    throw new InstallerProtocolException(
                        "installer.machine.removal_not_authorized");
                }

                return context;
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }
        catch
        {
            associationStore?.Dispose();
            rootGuard?.Dispose();
            throw;
        }
    }

    private async Task<ObservedContext> OpenObservedContextAsync(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        bool removalPlan,
        bool createMissingRoots,
        CancellationToken cancellationToken)
    {
        WindowsMachineDeploymentPlan plan = CreateCandidatePlan(
            request,
            manifest,
            removalPlan,
            cancellationToken);
        IWindowsMachineRootGuard? rootGuard = null;
        IWindowsMachineAssociationStore? associationStore = null;
        try
        {
            rootGuard = _backend.CreateRootGuard(plan, createMissingRoots);
            await rootGuard.EnsureProtectedAsync(plan, cancellationToken)
                .ConfigureAwait(false);
            associationStore = _backend.CreateAssociationStore(plan, rootGuard);
            InstallerMachineAssociationObservation observation =
                await associationStore.InspectAsync(cancellationToken).ConfigureAwait(false);
            observation.Validate();
            return new ObservedContext(
                plan,
                rootGuard,
                associationStore,
                observation,
                hasExactAssociation: false);
        }
        catch
        {
            associationStore?.Dispose();
            rootGuard?.Dispose();
            throw;
        }
    }

    private async Task RebindAsync(
        ObservedContext context,
        InstallerMachineAssociation association,
        CancellationToken cancellationToken)
    {
        if (context.Plan.Association == association)
        {
            return;
        }

        WindowsMachineDeploymentPlan rebound = context.IsProfileIndependent
            ? _backend.CreateProfileIndependentRemovalPlan(
                context.Plan.Request,
                context.Plan.Manifest,
                association)
            : _backend.CreatePlan(
                context.Plan.Request,
                context.Plan.Manifest,
                association,
                context.Plan.TargetProfileRoot,
                context.Plan.Request.Operation == InstallerOperation.Uninstall);
        await context.RootGuard.EnsureProtectedAsync(rebound, cancellationToken)
            .ConfigureAwait(false);
        IWindowsMachineAssociationStore replacement =
            _backend.CreateAssociationStore(rebound, context.RootGuard);
        context.AssociationStore.Dispose();
        context.Plan = rebound;
        context.AssociationStore = replacement;
    }

    private WindowsMachineDeploymentPlan CreateCandidatePlan(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        bool removalPlan,
        CancellationToken cancellationToken)
    {
        string targetProfile = _backend.ResolveTargetProfile(
            request.TargetSid,
            cancellationToken);
        InstallerMachineAssociation association = InstallerMachineAssociation.Create(
            request.TargetSid,
            _backend.CreateAuthenticationToken());
        return _backend.CreatePlan(
            request,
            manifest,
            association,
            targetProfile,
            removalPlan);
    }

    private void VerifyRemovedOrProfileIndependent(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            VerifyRemoved(
                CreateCandidatePlan(
                    request,
                    manifest,
                    removalPlan: true,
                    cancellationToken),
                cancellationToken);
        }
        catch (InstallerProtocolException exception) when (
            IsTargetProfileMissing(exception))
        {
            _backend.VerifyProfileIndependentRemovalPostcondition(cancellationToken);
        }
    }

    private static bool IsTargetProfileMissing(InstallerProtocolException exception) =>
        string.Equals(
            exception.DiagnosticCode,
            TargetProfileMissingDiagnosticCode,
            StringComparison.Ordinal);

    [DoesNotReturn]
    private static void Rethrow(Exception exception) =>
        ExceptionDispatchInfo.Capture(exception).Throw();

    private static T Rethrow<T>(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
        throw new InvalidOperationException("Unreachable exception dispatch path.");
    }

    private static void ValidateBoundary(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition? disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Release.Validate();
        release.Manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (disposition is { } value && !Enum.IsDefined(value))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.disposition_invalid");
        }

        if (!release.Manifest.Matches(release.Release)
            || !string.Equals(
                request.ExpectedPackageVersion,
                release.Manifest.ExpectedPackageVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                request.InstallerPayloadSha256,
                release.Manifest.InstallerPayloadSha256,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.release.identity_mismatch");
        }
    }

    private sealed class ObservedContext : IDisposable
    {
        private bool _disposed;

        internal ObservedContext(
            WindowsMachineDeploymentPlan plan,
            IWindowsMachineRootGuard rootGuard,
            IWindowsMachineAssociationStore associationStore,
            InstallerMachineAssociationObservation observation,
            bool hasExactAssociation,
            bool isProfileIndependent = false)
        {
            Plan = plan;
            RootGuard = rootGuard;
            AssociationStore = associationStore;
            Observation = observation;
            HasExactAssociation = hasExactAssociation;
            IsProfileIndependent = isProfileIndependent;
        }

        internal WindowsMachineDeploymentPlan Plan { get; set; }

        internal IWindowsMachineRootGuard RootGuard { get; }

        internal IWindowsMachineAssociationStore AssociationStore { get; set; }

        internal InstallerMachineAssociationObservation Observation { get; }

        internal bool HasExactAssociation { get; set; }

        internal bool IsProfileIndependent { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            AssociationStore.Dispose();
            RootGuard.Dispose();
            _disposed = true;
        }
    }
}

using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>Composes native original observations and SCM recovery under the authenticated outer leases.</summary>
internal sealed class WindowsMaintenanceOriginalSessionFactory : IWindowsMaintenanceOriginalSessionFactory
{
    private readonly WindowsMachineHelperMachineOperations _machines;
    private readonly IWindowsMaintenanceTrustStateReader _trust;
    private readonly IWindowsInstallerAuthorityLease _machine;
    private readonly IWindowsInstallerApplicationLease _application;
    private readonly IInstallerTransactionReader _transactions;
    private readonly IWindowsPackageManagerFacade _packages = new WindowsPackageManagerFacade();
    private readonly IWindowsInstalledPackageFootprintCatalog _roots = new WindowsInstalledPackageFootprintCatalog();

    internal WindowsMaintenanceOriginalSessionFactory(WindowsMachineHelperMachineOperations machines,
        IWindowsMaintenanceTrustStateReader trust, IWindowsInstallerAuthorityLease machine,
        IWindowsInstallerApplicationLease application, IInstallerTransactionReader transactions)
    {
        _machines = machines ?? throw new ArgumentNullException(nameof(machines));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
    }

    public async Task<IWindowsMaintenanceOriginalSession> OpenAsync(InstallerRequest request,
        IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        WindowsMachineHelperMachineOperations.ObservedContext context = await _machines
            .OpenOriginalRecoveryContextAsync(request, release, cancellationToken).ConfigureAwait(false);
        WindowsMaintenanceRecoveryAuthorityLease? authority = null;
        bool returned = false;
        try
        {
            string root = OriginalRoot(context.Plan, cancellationToken);
            authority = await WindowsMaintenanceRecoveryAuthorityLease.AcquireAsync(context.Plan, root, _machine, _application, cancellationToken).ConfigureAwait(false);
            var reader = new WindowsMaintenanceOriginalContentsReader(_packages, _roots, new WindowsPackageFootprintReader(),
                new WindowsMaintenancePayloadStateReader(), new WindowsMaintenancePackageStateReader(), context.AssociationStore, _trust, release);
            var session = new OriginalSession(context, authority, reader, _transactions, root,
                token => OriginalRoot(context.Plan, token));
            returned = true;
            return session;
        }
        finally
        {
            if (!returned) { authority?.Dispose(); context.Dispose(); }
        }
    }

    private string OriginalRoot(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallerInstalledPackage package = WindowsPackageRegistrationInspector.Inspect(_packages, plan.Request.TargetSid, plan.Manifest)
            ?? throw new InstallerProtocolException("installer.recovery.original_package_missing");
        if (!package.IsHealthy) { throw new InstallerProtocolException("installer.recovery.original_package_unhealthy"); }
        IReadOnlyList<string> roots = _roots.FindInstalledRoots(package.PackageFamilyName, package.PackageFullName, cancellationToken);
        if (roots.Count != 1 || !Path.IsPathFullyQualified(roots[0]))
        {
            throw new InstallerProtocolException("installer.recovery.original_package_root_invalid");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(roots[0]));
    }

    private sealed class OriginalSession(WindowsMachineHelperMachineOperations.ObservedContext context,
        WindowsMaintenanceRecoveryAuthorityLease authority, IWindowsMaintenanceOriginalContentsReader reader,
        IInstallerTransactionReader transactions, string packageRoot, Func<CancellationToken, string> currentRoot) : IWindowsMaintenanceOriginalSession
    {
        private readonly WindowsServiceConfigurationVerifier _service = new();
        private readonly WindowsServiceMutation _mutation = new();
        private bool _disposed;

        public async Task<WindowsMaintenanceOriginalBaseline> CaptureAsync(InstallerTransactionJournal intent, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            RequireRoot(cancellationToken);
            WindowsServicePreparationBaseline service = WindowsServicePreparationBaseline.Capture(context.Plan, intent, _service.Inspect(cancellationToken));
            WindowsMaintenanceOriginalContents contents = await reader.ReadAsync(context.Plan, cancellationToken).ConfigureAwait(false);
            RequirePinnedContents(contents);
            RequireService(service, cancellationToken);
            RequireRoot(cancellationToken);
            await authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            return new(WindowsMaintenanceOriginalBaseline.CurrentSchema, service, contents);
        }

        public async Task VerifyOriginalAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source,
            InstallerTransactionSnapshot? expectedPublic, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRoot(cancellationToken);
            RequirePinnedContents(baseline.Contents);
            await Guard(baseline, source, expectedPublic).VerifyOriginalStateAsync(context.Plan, baseline.Service, cancellationToken).ConfigureAwait(false);
            RequireRoot(cancellationToken);
        }

        public async Task RestoreServiceAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _mutation.RestorePreparedBaselineAsync(context.Plan, baseline.Service, Guard(baseline, source, source),
                source.Journal, cancellationToken).ConfigureAwait(false);
        }

        public async Task VerifyRestoredAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source,
            InstallerTransactionSnapshot? expectedPublic, CancellationToken cancellationToken)
        {
            await VerifyOriginalAsync(baseline, source, expectedPublic, cancellationToken).ConfigureAwait(false);
            RequireService(baseline.Service, cancellationToken);
            await VerifyOriginalAsync(baseline, source, expectedPublic, cancellationToken).ConfigureAwait(false);
        }

        private WindowsMaintenanceOriginalStateGuard Guard(WindowsMaintenanceOriginalBaseline baseline,
            InstallerTransactionSnapshot source, InstallerTransactionSnapshot? expectedPublic)
        {
            baseline.RequireBoundary(context.Plan, source.Journal);
            return new(baseline.Service, baseline.Contents, reader,
                new ExpectedTransactionView(transactions, source, expectedPublic), authority);
        }

        private void RequirePinnedContents(WindowsMaintenanceOriginalContents contents)
        {
            if (contents.PackageContentsSha256 != authority.PackageContentsSha256
                || !contents.Files.SequenceEqual(authority.PayloadFingerprints))
            {
                throw new InstallerProtocolException("installer.recovery.original_state_changed");
            }
        }
        private void RequireRoot(CancellationToken cancellationToken)
        {
            if (!string.Equals(packageRoot, currentRoot(cancellationToken), StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallerProtocolException("installer.recovery.original_package_root_changed");
            }
        }
        private void RequireService(WindowsServicePreparationBaseline baseline, CancellationToken cancellationToken)
        {
            WindowsServiceSnapshot actual = _service.Inspect(cancellationToken);
            if (!WindowsServiceConfigurationVerifier.ConfigurationMatches(actual.Configuration, baseline.Service.Configuration)
                || actual.DaclSddl != baseline.Service.DaclSddl || actual.RuntimeState != baseline.Service.RuntimeState)
            {
                throw new InstallerProtocolException("installer.recovery.original_service_changed");
            }
        }
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            try { authority.Dispose(); }
            finally { context.Dispose(); }
        }
    }

    // Terminal verification never lends mutation authority: the real public state must still equal
    // its expected terminal (or be absent after acknowledged-late clear). Only the immutable source
    // is projected for the existing original-evidence guard. SCM restoration always expects source.
    private sealed class ExpectedTransactionView(IInstallerTransactionReader inner, InstallerTransactionSnapshot source,
        InstallerTransactionSnapshot? expected) : IInstallerTransactionReader
    {
        public async Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            InstallerTransactionSnapshot? actual = await inner.LoadAsync(cancellationToken).ConfigureAwait(false);
            actual?.Validate();
            if (actual != expected) { throw new InstallerProtocolException("installer.recovery.transaction_changed"); }
            return source;
        }
    }
}

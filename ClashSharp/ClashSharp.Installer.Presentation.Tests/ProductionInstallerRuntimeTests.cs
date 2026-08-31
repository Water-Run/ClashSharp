using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class ProductionInstallerRuntimeTests
{
    [Theory]
    [InlineData(null, InstallerProductState.Available)]
    [InlineData(Version, InstallerProductState.Installed)]
    public async Task SupportedInspectionEnablesExpectedOrdinaryProductState(
        string? installedVersion,
        InstallerProductState expectedState)
    {
        var backend = new RecordingBackend(Inspection(
            supported: true,
            installedVersion,
            applicationRunning: false));
        using var runtime = new ProductionInstallerRuntime(backend);

        InstallerRuntimeReadiness readiness = await runtime.InspectReadinessAsync(
            CancellationToken.None);

        Assert.True(readiness.CanExecute);
        Assert.Equal("installer.runtime.ready", readiness.DiagnosticCode);
        Assert.Equal(expectedState, readiness.ProductState);
        Assert.Null(readiness.RecoveryOperation);
        Assert.Equal(Version, readiness.DisplayVersion);
        Assert.Equal(
            expectedState == InstallerProductState.Available
                ? [InstallerOperation.Install]
                : [InstallerOperation.Repair, InstallerOperation.Uninstall],
            readiness.AllowedOperations);
        Assert.All(readiness.Capabilities, static capability => Assert.True(capability.IsReady));
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task DurableInspectionExposesOnlyItsExactRecoveryOperation(
        InstallerOperation operation)
    {
        InstallerTransactionSnapshot durable = InstallerTransactionSnapshot.Create(
            InstallerTransactionJournal.Create(Request(operation)));
        var backend = new RecordingBackend(Inspection(
            supported: true,
            installedVersion: operation == InstallerOperation.Install ? null : Version,
            applicationRunning: false,
            durable));
        using var runtime = new ProductionInstallerRuntime(backend);

        InstallerRuntimeReadiness readiness = await runtime.InspectReadinessAsync(
            CancellationToken.None);

        Assert.True(readiness.CanExecute);
        Assert.Equal(InstallerProductState.RecoveryRequired, readiness.ProductState);
        Assert.Equal(operation, readiness.RecoveryOperation);
        Assert.Equal([operation], readiness.AllowedOperations);
        Assert.Equal("installer.runtime.recovery_required", readiness.DiagnosticCode);
    }

    [Fact]
    public async Task ExactPackageProcessBlocksEveryVisibleMutation()
    {
        var backend = new RecordingBackend(Inspection(
            supported: true,
            installedVersion: Version,
            applicationRunning: true));
        using var runtime = new ProductionInstallerRuntime(backend);

        InstallerRuntimeReadiness readiness = await runtime.InspectReadinessAsync(
            CancellationToken.None);

        Assert.False(readiness.CanExecute);
        Assert.Equal("installer.application_running", readiness.DiagnosticCode);
        Assert.Empty(readiness.AllowedOperations);
        Assert.Contains(readiness.Capabilities, static capability => !capability.IsReady);
    }

    [Fact]
    public async Task UnsupportedPlatformBlocksInstallButPreservesInstalledRemovalEntry()
    {
        var unavailableBackend = new RecordingBackend(Inspection(
            supported: false,
            installedVersion: null,
            applicationRunning: false));
        using var unavailable = new ProductionInstallerRuntime(unavailableBackend);

        InstallerRuntimeReadiness blocked = await unavailable.InspectReadinessAsync(
            CancellationToken.None);
        Assert.False(blocked.CanExecute);
        Assert.Equal("installer.environment.windows_11_required", blocked.DiagnosticCode);

        var installedBackend = new RecordingBackend(Inspection(
            supported: false,
            installedVersion: Version,
            applicationRunning: false));
        using var installed = new ProductionInstallerRuntime(installedBackend);
        InstallerRuntimeReadiness removal = await installed.InspectReadinessAsync(
            CancellationToken.None);
        Assert.True(removal.CanExecute);
        Assert.Equal(InstallerProductState.Installed, removal.ProductState);
        Assert.Equal([InstallerOperation.Uninstall], removal.AllowedOperations);
        Assert.Equal("仅安全卸载可用", removal.StatusTitle);
        Assert.All(removal.Capabilities, static capability => Assert.True(capability.IsReady));
    }

    [Fact]
    public async Task ExecuteForwardsOnlyOperationProgressAndCancellation()
    {
        var backend = new RecordingBackend(Inspection(true, null, false))
        {
            ExecuteResult = new InstallerExecutionResult(
                InstallerExecutionOutcome.Succeeded,
                "installer.completed",
                InstallerTransactionPhase.Verified,
                RecoveryPending: false),
        };
        using var runtime = new ProductionInstallerRuntime(backend);
        var progress = new Progress<InstallerProgress>();

        InstallerExecutionResult result = await runtime.ExecuteAsync(
            InstallerOperation.Repair,
            progress,
            CancellationToken.None);

        Assert.Equal(backend.ExecuteResult, result);
        Assert.Equal([InstallerOperation.Repair], backend.Operations);
        Assert.Same(progress, backend.Progress);
    }

    [Fact]
    public async Task MissingOrMalformedInspectionFailsClosed()
    {
        var missingBackend = new RecordingBackend(inspection: null);
        using var missing = new ProductionInstallerRuntime(missingBackend);
        InstallerProtocolException missingResult = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => missing.InspectReadinessAsync(
                CancellationToken.None));
        Assert.Equal(
            "installer.runtime.inspection_result_missing",
            missingResult.DiagnosticCode);

        var malformedBackend = new RecordingBackend(new InstallerRuntimeInspection(
            new InstallerEnvironmentSnapshot(
                IsSupported: true,
                InstalledPackageVersion: null,
                IsApplicationRunning: false,
                BlockingDiagnosticCode: "installer.should_not_exist"),
            DurableTransaction: null,
            Version));
        using var malformed = new ProductionInstallerRuntime(malformedBackend);
        InstallerProtocolException invalid = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => malformed.InspectReadinessAsync(
                CancellationToken.None));
        Assert.Equal(
            "installer.runtime.inspection_result_invalid",
            invalid.DiagnosticCode);
    }

    [Fact]
    public async Task CancellationAndDisposeStopBeforeBackendWork()
    {
        var backend = new RecordingBackend(Inspection(true, null, false));
        var runtime = new ProductionInstallerRuntime(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.InspectReadinessAsync(cancellation.Token));
        Assert.Equal(0, backend.InspectCount);

        runtime.Dispose();
        runtime.Dispose();
        Assert.Equal(1, backend.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            runtime.InspectReadinessAsync(CancellationToken.None));
    }

    private const string Version = "1.2.3.4";
    private const string PayloadHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    private static InstallerRuntimeInspection Inspection(
        bool supported,
        string? installedVersion,
        bool applicationRunning,
        InstallerTransactionSnapshot? durable = null) => new(
            new InstallerEnvironmentSnapshot(
                supported,
                installedVersion,
                applicationRunning,
                supported ? null : "installer.environment.windows_11_required"),
            durable,
            Version);

    private static InstallerRequest Request(InstallerOperation operation) => new(
        operation,
        TargetSid,
        AllowReassociation: false,
        Version,
        PayloadHash);

    private sealed class RecordingBackend : IInstallerRuntimeBackend
    {
        private readonly InstallerRuntimeInspection? _inspection;

        internal RecordingBackend(InstallerRuntimeInspection? inspection)
        {
            _inspection = inspection;
        }

        internal int InspectCount { get; private set; }

        internal List<InstallerOperation> Operations { get; } = [];

        internal IProgress<InstallerProgress>? Progress { get; private set; }

        internal InstallerExecutionResult ExecuteResult { get; init; } = new(
            InstallerExecutionOutcome.Blocked,
            "installer.test.blocked",
            LastDurablePhase: null,
            RecoveryPending: false);

        internal int DisposeCount { get; private set; }

        public Task<InstallerRuntimeInspection> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCount++;
            return Task.FromResult(_inspection!);
        }

        public Task<InstallerExecutionResult> ExecuteAsync(
            InstallerOperation operation,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add(operation);
            Progress = progress;
            return Task.FromResult(ExecuteResult);
        }

        public void Dispose() => DisposeCount++;
    }
}

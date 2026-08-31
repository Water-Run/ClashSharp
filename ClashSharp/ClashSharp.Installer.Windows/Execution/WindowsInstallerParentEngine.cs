using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Certificates;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Execution;

internal interface IWindowsInstallerExecutionSession : IAsyncDisposable
{
    Task<InstallerExecutionResult> ExecuteAsync(
        InstallerRequest request,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken);
}

internal interface IWindowsInstallerExecutionSessionFactory
{
    Task<IWindowsInstallerExecutionSession> CreateAsync(
        CancellationToken cancellationToken);
}

internal interface IWindowsInstallerParentInspector
{
    Task<InstallerRuntimeInspection> InspectAsync(
        InstallerRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns trusted request construction for the unelevated Installer parent and creates one bounded
/// coordinator/helper session per operation.
/// </summary>
public sealed class WindowsInstallerParentEngine : IInstallerRuntimeBackend
{
    private readonly object _lifetimeSync = new();
    private readonly InstallerReleaseManifest _manifest;
    private readonly string _targetSid;
    private readonly IWindowsInstallerExecutionSessionFactory _sessionFactory;
    private readonly IWindowsInstallerParentInspector _inspector;
    private bool _active;
    private bool _disposed;

    private WindowsInstallerParentEngine(
        InstallerReleaseManifest manifest,
        string targetSid,
        IWindowsInstallerExecutionSessionFactory sessionFactory,
        IWindowsInstallerParentInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(inspector);
        manifest.Validate();
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        _manifest = manifest;
        _targetSid = targetSid;
        _sessionFactory = sessionFactory;
        _inspector = inspector;
    }

    /// <summary>
    /// Creates the parent engine from the exact embedded manifest bytes and current Installer path.
    /// No package, certificate, service, UAC, or protected-state mutation occurs during creation.
    /// </summary>
    /// <param name="embeddedManifestBytes">Exact signed-resource manifest bytes.</param>
    /// <param name="installerExecutablePath">Path of the currently running Installer executable.</param>
    public static WindowsInstallerParentEngine CreateDefault(
        ReadOnlyMemory<byte> embeddedManifestBytes,
        string installerExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerExecutablePath);
        if (embeddedManifestBytes.IsEmpty)
        {
            throw new InstallerProtocolException("installer.release.manifest_missing");
        }

        byte[] manifestBytes = embeddedManifestBytes.ToArray();
        InstallerReleaseManifest manifest = InstallerReleaseManifestCodec.Parse(manifestBytes);
        string targetSid = WindowsInstallerCurrentUser.GetSid();
        var sessionFactory = new WindowsInstallerExecutionSessionFactory(
            manifestBytes,
            manifest,
            installerExecutablePath,
            targetSid,
            WindowsInstallerCurrentUser.GetSid);
        var inspector = new WindowsInstallerParentInspector(
            manifest,
            targetSid,
            installerExecutablePath,
            new WindowsInstallerExecutableTrustVerifier(manifest));
        return new WindowsInstallerParentEngine(
            manifest,
            targetSid,
            sessionFactory,
            inspector);
    }

    /// <summary>Gets the release version derived from the embedded manifest.</summary>
    public string ReleaseVersion => _manifest.ExpectedPackageVersion;

    /// <summary>
    /// Reads platform, exact current-user package/process state, and the protected recovery journal
    /// without creating a helper or acquiring mutation authority.
    /// </summary>
    /// <param name="cancellationToken">Cancels the bounded read-only inspection.</param>
    public async Task<InstallerRuntimeInspection> InspectAsync(
        CancellationToken cancellationToken)
    {
        EnterOrThrow();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new InstallerRequest(
                InstallerOperation.Uninstall,
                _targetSid,
                AllowReassociation: false,
                _manifest.ExpectedPackageVersion,
                _manifest.InstallerPayloadSha256);
            InstallerRuntimeInspection inspection = await _inspector
                .InspectAsync(request, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InstallerProtocolException(
                    "installer.runtime.inspection_result_missing");
            ValidateInspection(inspection);
            return inspection;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Executes one exact operation. Concurrent calls are rejected before creating a helper or
    /// touching any package or machine capability.
    /// </summary>
    /// <param name="operation">Install, Repair, or Uninstall.</param>
    /// <param name="progress">Optional best-effort progress observer.</param>
    /// <param name="cancellationToken">Cancels before durable intent or requests safe recovery.</param>
    public async Task<InstallerExecutionResult> ExecuteAsync(
        InstallerOperation operation,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new InstallerProtocolException(
                "installer.request.operation_invalid");
        }

        if (!TryEnter())
        {
            return new InstallerExecutionResult(
                InstallerExecutionOutcome.Blocked,
                "installer.concurrent_action_rejected",
                LastDurablePhase: null,
                RecoveryPending: false);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new InstallerRequest(
                operation,
                _targetSid,
                AllowReassociation: false,
                _manifest.ExpectedPackageVersion,
                _manifest.InstallerPayloadSha256);
            request.Validate();
            await using IWindowsInstallerExecutionSession session =
                await _sessionFactory
                    .CreateAsync(cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InstallerProtocolException(
                    "installer.runtime.execution_session_missing");
            return await session
                .ExecuteAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    private bool TryEnter()
    {
        lock (_lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active)
            {
                return false;
            }

            _active = true;
            return true;
        }
    }

    private void EnterOrThrow()
    {
        if (!TryEnter())
        {
            throw new InstallerProtocolException(
                "installer.concurrent_action_rejected");
        }
    }

    private void Exit()
    {
        lock (_lifetimeSync)
        {
            _active = false;
        }
    }

    private void ValidateInspection(InstallerRuntimeInspection inspection)
    {
        inspection.Validate();
        if (!string.Equals(
                inspection.ReleaseVersion,
                _manifest.ExpectedPackageVersion,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.runtime.inspection_result_invalid");
        }
    }

    /// <summary>Prevents new sessions; an already-running bounded session is allowed to finish.</summary>
    public void Dispose()
    {
        lock (_lifetimeSync)
        {
            _disposed = true;
        }
    }

    internal static WindowsInstallerParentEngine CreateForTesting(
        InstallerReleaseManifest manifest,
        string targetSid,
        IWindowsInstallerExecutionSessionFactory sessionFactory,
        IWindowsInstallerParentInspector? inspector = null) =>
        new(
            manifest,
            targetSid,
            sessionFactory,
            inspector ?? UnavailableWindowsInstallerParentInspector.Instance);
}

internal sealed class WindowsInstallerParentInspector : IWindowsInstallerParentInspector
{
    private readonly InstallerReleaseManifest _manifest;
    private readonly string _targetSid;
    private readonly string _installerExecutablePath;
    private readonly IWindowsInstallerExecutableTrustVerifier _trustVerifier;

    internal WindowsInstallerParentInspector(
        InstallerReleaseManifest manifest,
        string targetSid,
        string installerExecutablePath,
        IWindowsInstallerExecutableTrustVerifier trustVerifier)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerExecutablePath);
        ArgumentNullException.ThrowIfNull(trustVerifier);
        manifest.Validate();
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        _manifest = manifest;
        _targetSid = targetSid;
        _installerExecutablePath = installerExecutablePath;
        _trustVerifier = trustVerifier;
    }

    public async Task<InstallerRuntimeInspection> InspectAsync(
        InstallerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.TargetSid, _targetSid, StringComparison.Ordinal)
            || !string.Equals(
                request.ExpectedPackageVersion,
                _manifest.ExpectedPackageVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                request.InstallerPayloadSha256,
                _manifest.InstallerPayloadSha256,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.runtime.inspection_request_mismatch");
        }

        using IWindowsInstallerExecutableTrustLease trustLease = await _trustVerifier
            .VerifyAsync(_installerExecutablePath, cancellationToken)
            .ConfigureAwait(false);
        var environment = new WindowsInstallerEnvironment(_manifest);
        using WindowsInstallerProtectedTransactionReader transactionReader =
            WindowsInstallerProtectedTransactionReader.CreateDefault(_targetSid);
        InstallerEnvironmentSnapshot environmentSnapshot = await environment
            .InspectAsync(request, cancellationToken)
            .ConfigureAwait(false);
        InstallerTransactionSnapshot? durable = await transactionReader
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        durable?.Validate();
        if (durable is { } pending
            && (!string.Equals(
                    pending.Journal.TargetSid,
                    request.TargetSid,
                    StringComparison.Ordinal)
                || !string.Equals(
                    pending.Journal.ExpectedPackageVersion,
                    request.ExpectedPackageVersion,
                    StringComparison.Ordinal)
                || !string.Equals(
                    pending.Journal.InstallerPayloadSha256,
                    request.InstallerPayloadSha256,
                    StringComparison.Ordinal)))
        {
            throw new InstallerProtocolException(
                "installer.transaction.release_conflict");
        }

        var inspection = new InstallerRuntimeInspection(
            environmentSnapshot,
            durable,
            _manifest.ExpectedPackageVersion);
        inspection.Validate();
        return inspection;
    }
}

internal sealed class UnavailableWindowsInstallerParentInspector
    : IWindowsInstallerParentInspector
{
    internal static UnavailableWindowsInstallerParentInspector Instance { get; } = new();

    private UnavailableWindowsInstallerParentInspector()
    {
    }

    public Task<InstallerRuntimeInspection> InspectAsync(
        InstallerRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class WindowsInstallerExecutionSessionFactory
    : IWindowsInstallerExecutionSessionFactory
{
    private readonly byte[] _embeddedManifestBytes;
    private readonly InstallerReleaseManifest _manifest;
    private readonly string _installerExecutablePath;
    private readonly string _targetSid;
    private readonly Func<string?> _currentSid;

    internal WindowsInstallerExecutionSessionFactory(
        ReadOnlyMemory<byte> embeddedManifestBytes,
        InstallerReleaseManifest manifest,
        string installerExecutablePath,
        string targetSid,
        Func<string?> currentSid)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerExecutablePath);
        ArgumentNullException.ThrowIfNull(currentSid);
        if (embeddedManifestBytes.IsEmpty)
        {
            throw new InstallerProtocolException("installer.release.manifest_missing");
        }

        manifest.Validate();
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        _embeddedManifestBytes = embeddedManifestBytes.ToArray();
        _manifest = manifest;
        _installerExecutablePath = installerExecutablePath;
        _targetSid = targetSid;
        _currentSid = currentSid;
    }

    public async Task<IWindowsInstallerExecutionSession> CreateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsInstallerProtectedTransactionReader? transactionReader = null;
        WindowsMachineHelperBroker? broker = null;
        try
        {
            var environment = new WindowsInstallerEnvironment(_manifest);
            var releaseVerifier = new WindowsInstallerReleaseVerifier(
                _embeddedManifestBytes,
                _installerExecutablePath);
            var certificatePostcondition = new WindowsInstallerCertificatePostcondition();
            var packageMutation = new VerifiedInstallerPackageMutation(
                new WindowsCurrentUserPackageStoreAdapter());
            transactionReader = WindowsInstallerProtectedTransactionReader.CreateDefault(
                _targetSid);
            broker = WindowsMachineHelperBroker.CreateDefault(
                _installerExecutablePath,
                _manifest);
            var elevatedMachine = new WindowsElevatedMachineAdapter(broker, _currentSid);
            var coordinator = new InstallerCoordinator(
                environment,
                releaseVerifier,
                certificatePostcondition,
                packageMutation,
                elevatedMachine,
                elevatedMachine,
                transactionReader);
            IWindowsInstallerExecutionSession session =
                new WindowsInstallerExecutionSession(
                    coordinator,
                    transactionReader,
                    broker);
            return session;
        }
        catch
        {
            transactionReader?.Dispose();
            if (broker is not null)
            {
                await broker.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }
}

internal sealed class WindowsInstallerExecutionSession : IWindowsInstallerExecutionSession
{
    private readonly InstallerCoordinator _coordinator;
    private readonly WindowsInstallerProtectedTransactionReader _transactionReader;
    private readonly WindowsMachineHelperBroker _broker;
    private bool _disposed;

    internal WindowsInstallerExecutionSession(
        InstallerCoordinator coordinator,
        WindowsInstallerProtectedTransactionReader transactionReader,
        WindowsMachineHelperBroker broker)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(transactionReader);
        ArgumentNullException.ThrowIfNull(broker);
        _coordinator = coordinator;
        _transactionReader = transactionReader;
        _broker = broker;
    }

    public Task<InstallerExecutionResult> ExecuteAsync(
        InstallerRequest request,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _coordinator.ExecuteAsync(request, progress, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _coordinator.Dispose();
        try
        {
            await _broker.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _transactionReader.Dispose();
            _disposed = true;
        }
    }
}

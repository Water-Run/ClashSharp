using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsInstallerExecutableTrustVerifier
{
    Task<IWindowsInstallerExecutableTrustLease> VerifyAsync(
        string executablePath,
        CancellationToken cancellationToken);
}

internal sealed record WindowsMachineHelperBrokerLimits(
    TimeSpan ElevationTimeout,
    TimeSpan ConnectionTimeout,
    TimeSpan CommandTimeout,
    TimeSpan TerminationTimeout)
{
    internal static WindowsMachineHelperBrokerLimits Default { get; } = new(
        TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromSeconds(10));

    internal void Validate()
    {
        ValidateTimeout(ElevationTimeout);
        ValidateTimeout(ConnectionTimeout);
        ValidateTimeout(CommandTimeout);
        ValidateTimeout(TerminationTimeout);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }
}

/// <summary>
/// Reuses one PID-bound elevated self-helper pipe for the exact transaction and one UAC crossing.
/// </summary>
internal sealed class WindowsMachineHelperBroker :
    IWindowsMachineHelperBroker,
    IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _installerExecutablePath;
    private readonly IWindowsInstallerExecutableTrustVerifier _trustVerifier;
    private readonly IWindowsMachineHelperServerFactory _serverFactory;
    private readonly IWindowsRunAsProcessLauncher _launcher;
    private readonly WindowsMachineHelperBrokerLimits _limits;
    private readonly Func<int> _currentProcessId;
    private readonly Lazy<Task> _disposal;
    private int _disposalRequested;
    private Session? _session;
    private bool _completed;
    private bool _faulted;
    private bool _disposed;

    internal WindowsMachineHelperBroker(
        string installerExecutablePath,
        IWindowsInstallerExecutableTrustVerifier trustVerifier,
        IWindowsMachineHelperServerFactory serverFactory,
        IWindowsRunAsProcessLauncher launcher,
        WindowsMachineHelperBrokerLimits limits,
        Func<int> currentProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerExecutablePath);
        ArgumentNullException.ThrowIfNull(trustVerifier);
        ArgumentNullException.ThrowIfNull(serverFactory);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(currentProcessId);
        limits.Validate();
        _installerExecutablePath = installerExecutablePath;
        _trustVerifier = trustVerifier;
        _serverFactory = serverFactory;
        _launcher = launcher;
        _limits = limits;
        _currentProcessId = currentProcessId;
        _disposal = new(DisposeCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal static WindowsMachineHelperBroker CreateDefault(
        string installerExecutablePath,
        InstallerReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        return new WindowsMachineHelperBroker(
            installerExecutablePath,
            new WindowsInstallerExecutableTrustVerifier(manifest),
            new WindowsMachineHelperServerFactory(),
            new WindowsRunAsProcessLauncher(),
            WindowsMachineHelperBrokerLimits.Default,
            static () => Environment.ProcessId);
    }

    public async Task<InstallerMachineHelperResult> ExecuteAsync(
        InstallerMachineHelperCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed || Volatile.Read(ref _disposalRequested) != 0, this);
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || Volatile.Read(ref _disposalRequested) != 0, this);
            if (_completed)
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_completed");
            }

            if (_faulted)
            {
                throw new InstallerStateUncertainException(
                    "installer.machine_helper.session_unusable");
            }

            _session ??= await StartSessionAsync(command).ConfigureAwait(false);
            if (!string.Equals(
                    _session.TransactionId,
                    command.TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_transaction_mismatch");
            }

            InstallerMachineHelperResult result;
            try
            {
                using CancellationTokenSource deadline = CreateDeadline(_limits.CommandTimeout);
                await InstallerMachineHelperFraming
                    .WriteCommandAsync(_session.Server.Transport, command, deadline.Token)
                    .ConfigureAwait(false);
                result = await InstallerMachineHelperFraming
                    .ReadResultAsync(_session.Server.Transport, deadline.Token)
                    .ConfigureAwait(false);
                _ = result.ValidateAgainst(command);
            }
            catch (OperationCanceledException)
            {
                await FaultSessionAsync().ConfigureAwait(false);
                throw new InstallerStateUncertainException(
                    "installer.machine_helper.command_timeout");
            }
            catch (InstallerProtocolException)
            {
                await FaultSessionAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                await FaultSessionAsync().ConfigureAwait(false);
                throw new InstallerStateUncertainException(
                    "installer.machine_helper.response_unconfirmed");
            }

            if (command.Verb == InstallerMachineHelperVerb.Clear
                && result.Outcome == InstallerMachineHelperOutcome.Succeeded)
            {
                await CompleteSessionAsync().ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes ownership of an already authenticated dedicated connection after its exact Prepared
    /// handoff. Only a fresh, unpublished broker may adopt; no new pipe or UAC launch is performed.
    /// </summary>
    internal void Adopt(InstallerTransactionSnapshot continuation, IWindowsMachineHelperServer server,
        IWindowsElevatedHelperProcess process, IWindowsInstallerExecutableTrustLease trustLease)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(trustLease);
        continuation.Validate();
        if (_disposed || Volatile.Read(ref _disposalRequested) != 0 || _faulted || _completed || _session is not null
            || continuation.Journal.AllowReassociation
            || continuation.Journal.Operation is not (InstallerOperation.Install or InstallerOperation.Repair)
            || continuation.Journal.Phase != InstallerTransactionPhase.Prepared)
        {
            throw new InstallerProtocolException("installer.owner_transfer.handoff_invalid");
        }
        _session = new(continuation.Journal.TransactionId, server, process, trustLease);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposalRequested, 1);
        return new(_disposal.Value);
    }

    /// <summary>Adopts only an authenticated dedicated uninstall session, including recovered phases.</summary>
    internal void AdoptRetiredUninstall(InstallerTransactionSnapshot ready, IWindowsMachineHelperServer server,
        IWindowsElevatedHelperProcess process, IWindowsInstallerExecutableTrustLease trustLease)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(trustLease);
        InstallerRetiredUninstallProtocol.Validate(ready);
        if (_disposed || Volatile.Read(ref _disposalRequested) != 0 || _faulted || _completed || _session is not null)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.handoff_invalid");
        }
        _session = new(ready.Journal.TransactionId, server, process, trustLease);
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await DisposeSessionAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<Session> StartSessionAsync(
        InstallerMachineHelperCommand firstCommand)
    {
        int parentProcessId = _currentProcessId();
        InstallerMachineHelperBootstrap bootstrap = InstallerMachineHelperBootstrap.Create(
            firstCommand.ToInvocation(),
            parentProcessId);
        IWindowsMachineHelperServer server = _serverFactory.Create(bootstrap);
        IWindowsInstallerExecutableTrustLease? trustLease = null;
        IWindowsElevatedHelperProcess? process = null;
        try
        {
            using CancellationTokenSource elevationDeadline = CreateDeadline(
                _limits.ElevationTimeout);
            trustLease = await _trustVerifier
                .VerifyAsync(_installerExecutablePath, elevationDeadline.Token)
                .ConfigureAwait(false);
            process = await _launcher
                .StartAsync(
                    trustLease.ExecutablePath,
                    bootstrap,
                    elevationDeadline.Token)
                .ConfigureAwait(false);
            using CancellationTokenSource connectionDeadline = CreateDeadline(
                _limits.ConnectionTimeout);
            await server
                .WaitForConnectionAsync(connectionDeadline.Token)
                .ConfigureAwait(false);
            server.VerifyClient(process.ProcessId);
            return new(firstCommand.TransactionId, server, process, trustLease);
        }
        catch (OperationCanceledException)
        {
            await DisposeUnadmittedSessionAsync(server, process, trustLease).ConfigureAwait(false);
            throw process is null
                ? new InstallerProtocolException(
                    "installer.elevation.trust_or_launch_timeout")
                : new InstallerStateUncertainException(
                    "installer.machine_helper.connection_timeout");
        }
        catch
        {
            await DisposeUnadmittedSessionAsync(server, process, trustLease).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompleteSessionAsync()
    {
        Session session = _session
            ?? throw new InstallerProtocolException(
                "installer.machine_helper.session_missing");
        try
        {
            using CancellationTokenSource deadline = CreateDeadline(
                _limits.TerminationTimeout);
            await session.Process
                .WaitForExitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FaultSessionAsync().ConfigureAwait(false);
            throw new InstallerStateUncertainException(
                "installer.elevation.termination_unconfirmed");
        }

        await DisposeSessionAsync().ConfigureAwait(false);
        _completed = true;
    }

    private async Task FaultSessionAsync()
    {
        _faulted = true;
        Session? session = _session;
        if (session is null)
        {
            return;
        }

        await session.DisposeTransportAsync().ConfigureAwait(false);
        try
        {
            using CancellationTokenSource deadline = CreateDeadline(
                _limits.TerminationTimeout);
            await session.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await DisposeSessionAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The helper may still own a privileged mutation. Keep its process handle and the
            // immutable signed-image lease until independent process termination is observed.
        }
    }

    private async Task DisposeSessionAsync()
    {
        Session? session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisposeTransportAsync().ConfigureAwait(false);
        }
        finally
        {
            // Closing the transport requests helper shutdown; it does not prove that a native
            // operation has stopped. Window shutdown awaits this owned drain before releasing pins.
            if (!session.Process.HasExited)
            {
                await session.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            session.DisposePinnedResources();
            _session = null;
        }
    }

    private static CancellationTokenSource CreateDeadline(TimeSpan timeout)
    {
        var source = new CancellationTokenSource();
        source.CancelAfter(timeout);
        return source;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);

    private static async Task DisposeUnadmittedSessionAsync(IWindowsMachineHelperServer server,
        IWindowsElevatedHelperProcess? process, IWindowsInstallerExecutableTrustLease? trust)
    {
        try
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { process?.Dispose(); }
            finally { trust?.Dispose(); }
        }
    }

    private sealed class Session
    {
        private bool _transportDisposed;
        private bool _pinnedResourcesDisposed;

        internal Session(
            string transactionId,
            IWindowsMachineHelperServer server,
            IWindowsElevatedHelperProcess process,
            IWindowsInstallerExecutableTrustLease trustLease)
        {
            TransactionId = transactionId;
            Server = server;
            Process = process;
            TrustLease = trustLease;
        }

        internal string TransactionId { get; }

        internal IWindowsMachineHelperServer Server { get; }

        internal IWindowsElevatedHelperProcess Process { get; }

        internal IWindowsInstallerExecutableTrustLease TrustLease { get; }

        internal async ValueTask DisposeTransportAsync()
        {
            if (_transportDisposed)
            {
                return;
            }

            await Server.DisposeAsync().ConfigureAwait(false);
            _transportDisposed = true;
        }

        internal void DisposePinnedResources()
        {
            if (_pinnedResourcesDisposed)
            {
                return;
            }

            _pinnedResourcesDisposed = true;
            try { Process.Dispose(); }
            finally { TrustLease.Dispose(); }
        }
    }
}

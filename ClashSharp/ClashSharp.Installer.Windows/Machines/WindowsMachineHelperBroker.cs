using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

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
            process?.Dispose();
            trustLease?.Dispose();
            await server.DisposeAsync().ConfigureAwait(false);
            throw process is null
                ? new InstallerProtocolException(
                    "installer.elevation.trust_or_launch_timeout")
                : new InstallerStateUncertainException(
                    "installer.machine_helper.connection_timeout");
        }
        catch
        {
            process?.Dispose();
            trustLease?.Dispose();
            await server.DisposeAsync().ConfigureAwait(false);
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
        _session = null;
        if (session is null)
        {
            return;
        }

        await session.DisposeTransportAsync().ConfigureAwait(false);
        if (session.Process.HasExited)
        {
            session.DisposePinnedResources();
        }
        else
        {
            _ = ReleasePinnedResourcesAfterExitAsync(session);
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

    private static async Task ReleasePinnedResourcesAfterExitAsync(Session session)
    {
        try
        {
            await session.Process
                .WaitForExitAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Process termination can no longer affect protocol output after the pipe is closed.
        }
        finally
        {
            session.DisposePinnedResources();
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

            Process.Dispose();
            TrustLease.Dispose();
            _pinnedResourcesDisposed = true;
        }
    }
}

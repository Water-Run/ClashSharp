using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>Counts acquisitions and releases so a leaked app-lifetime lease is observable.</summary>
internal sealed class EngineApplicationLock : IWindowsInstallerApplicationLock
{
    internal int Acquisitions { get; private set; }

    internal int Releases { get; private set; }

    internal bool Held { get; private set; }

    internal Exception? AcquireFailure { get; set; }

    public IDisposable Acquire(string targetSid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Held)
        {
            throw new InvalidOperationException("The application lease was acquired twice.");
        }

        if (AcquireFailure is not null)
        {
            throw AcquireFailure;
        }

        Held = true;
        Acquisitions++;
        return new Lease(this);
    }

    private sealed class Lease(EngineApplicationLock owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Held = false;
            owner.Releases++;
        }
    }
}

/// <summary>In-memory protected preferences store that records every durable mutation.</summary>
internal sealed class EnginePreferencesStore : IWindowsInstallerPreferencesStore
{
    internal WindowsInstallerPreferencesDocument? Pending { get; set; }

    internal WindowsInstallerPreferencesDocument? Installed { get; set; }

    internal int Stages { get; private set; }

    internal int Completions { get; private set; }

    internal int Removals { get; private set; }

    public WindowsInstallerPreferencesDocument? ReadPending() => Pending;

    public WindowsInstallerPreferencesDocument? ReadInstalled() => Installed;

    public void Stage(WindowsInstallerPreferencesDocument document, WindowsInstallerPreferencesDocument? expected)
    {
        if (!Equals(expected, Pending))
        {
            throw new InstallerProtocolException("installer.preferences.store_changed");
        }

        Pending = document;
        Stages++;
    }

    public void Complete(WindowsInstallerPreferencesDocument document)
    {
        if (!Equals(document, Pending))
        {
            throw new InstallerProtocolException("installer.preferences.store_changed");
        }

        Installed = document;
        Pending = null;
        Completions++;
    }

    public void RemoveInstalled(WindowsInstallerPreferencesDocument? expected)
    {
        if (!Equals(expected, Installed))
        {
            throw new InstallerProtocolException("installer.preferences.store_changed");
        }

        Installed = null;
        Removals++;
    }

    public void RemovePending(WindowsInstallerPreferencesDocument? expected)
    {
        if (!Equals(expected, Pending))
        {
            throw new InstallerProtocolException("installer.preferences.store_changed");
        }

        Pending = null;
        Removals++;
    }
}

/// <summary>Shell double that can fail removal with any exception.</summary>
internal sealed class EnginePreferencesShell : IWindowsInstallerShellIntegration
{
    internal Exception? RemovalFailure { get; set; }

    internal int RemoveCalls { get; private set; }

    public Task ApplyAsync(
        InstallerPreferences preferences,
        string applicationId,
        string iconPath,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(string applicationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveCalls++;
        return RemovalFailure is null ? Task.CompletedTask : Task.FromException(RemovalFailure);
    }
}

/// <summary>User-setup double with injectable failure and a hook that runs on the awaiting path.</summary>
internal sealed class EnginePreferencesSetup : IWindowsInstallerUserSetup
{
    internal Exception? Failure { get; set; }

    internal Action? OnApply { get; set; }

    internal int Calls { get; private set; }

    public async Task<InstallerPreferencesWarnings> ApplyAsync(
        WindowsInstallerPreferencesDocument document,
        CancellationToken cancellationToken)
    {
        Calls++;
        OnApply?.Invoke();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
        {
            throw Failure;
        }

        return InstallerPreferencesWarnings.None;
    }
}

/// <summary>Execution session that records requests and disposal.</summary>
internal class EngineSession : IWindowsInstallerExecutionSession
{
    private readonly EngineSessionFactory _owner;

    internal EngineSession(EngineSessionFactory owner)
    {
        _owner = owner;
    }

    public Task<InstallerExecutionResult> ExecuteAsync(
        InstallerRequest request,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        request.Validate();
        _owner.Requests.Add(request);
        return _owner.RunAsync(EngineStage.SessionExecute, _owner.ExecuteResult, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _owner.Disposals++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Session that additionally offers original restoration.</summary>
internal sealed class EngineRestoringSession : EngineSession, IWindowsInstallerOriginalRestorationSession
{
    private readonly EngineSessionFactory _owner;

    internal EngineRestoringSession(EngineSessionFactory owner)
        : base(owner)
    {
        _owner = owner;
    }

    public Task<InstallerExecutionResult> RestoreOriginalAsync(
        InstallerRequest request,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        request.Validate();
        _owner.Requests.Add(request);
        return _owner.RunAsync(EngineStage.SessionRestore, _owner.RestoreResult, cancellationToken);
    }
}

public enum EngineStage
{
    Inspect,
    CreateSession,
    SessionExecute,
    SessionRestore,
    RetiredUninstall,
    OwnerTransfer,
}

/// <summary>Factory implementing every optional engine capability behind one observable gate.</summary>
internal sealed class EngineSessionFactory : IWindowsInstallerExecutionSessionFactory,
    IWindowsInstallerOwnerTransferSessionFactory,
    IWindowsInstallerRetiredUninstallSessionFactory,
    IWindowsInstallerOriginalRestorationSessionFactory
{
    internal static InstallerExecutionResult Success { get; } = new(
        InstallerExecutionOutcome.Succeeded,
        "installer.completed",
        InstallerTransactionPhase.Verified,
        RecoveryPending: false);

    internal List<InstallerRequest> Requests { get; } = [];

    internal int Creates { get; private set; }

    internal int Disposals { get; set; }

    internal int Transfers { get; private set; }

    internal int Retired { get; private set; }

    internal bool RestoringSessions { get; init; } = true;

    internal InstallerExecutionResult ExecuteResult { get; set; } = Success;

    internal InstallerExecutionResult RestoreResult { get; set; } = Success;

    internal InstallerExecutionResult RetiredResult { get; set; } = Success;

    internal InstallerExecutionResult TransferResult { get; set; } = Success;

    internal Exception? CreateFailure { get; set; }

    internal Exception? ExecuteFailure { get; set; }

    internal Exception? RetiredFailure { get; set; }

    internal Exception? TransferFailure { get; set; }

    internal Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>>? LastConfirm { get; private set; }

    /// <summary>When set, the first entry signals <see cref="Entered"/> and waits for <see cref="Release"/>.</summary>
    internal bool Gated { get; set; }

    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called at every stage; tests cancel or fail at an exact await.</summary>
    internal Action<EngineStage>? OnStage { get; set; }

    internal async Task<InstallerExecutionResult> RunAsync(
        EngineStage stage,
        InstallerExecutionResult result,
        CancellationToken cancellationToken)
    {
        OnStage?.Invoke(stage);
        if (Gated)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = stage switch
        {
            EngineStage.SessionExecute => ExecuteFailure,
            EngineStage.RetiredUninstall => RetiredFailure,
            EngineStage.OwnerTransfer => TransferFailure,
            _ => null,
        };
        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }

    public async Task<IWindowsInstallerExecutionSession> CreateAsync(CancellationToken cancellationToken)
    {
        OnStage?.Invoke(EngineStage.CreateSession);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        Creates++;
        return RestoringSessions ? new EngineRestoringSession(this) : new EngineSession(this);
    }

    public Task<InstallerExecutionResult> UninstallRetiredAccountAsync(
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        Retired++;
        return RunAsync(EngineStage.RetiredUninstall, RetiredResult, cancellationToken);
    }

    public Task<InstallerExecutionResult> TransferAndExecuteAsync(
        Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        Transfers++;
        LastConfirm = confirm;
        return RunAsync(EngineStage.OwnerTransfer, TransferResult, cancellationToken);
    }
}

/// <summary>Factory that supports only ordinary execution, as an older composition would.</summary>
internal sealed class PlainEngineSessionFactory : IWindowsInstallerExecutionSessionFactory
{
    internal int Creates { get; private set; }

    public Task<IWindowsInstallerExecutionSession> CreateAsync(CancellationToken cancellationToken)
    {
        Creates++;
        IWindowsInstallerExecutionSession session = new PlainSession();
        return Task.FromResult(session);
    }

    private sealed class PlainSession : IWindowsInstallerExecutionSession
    {
        public Task<InstallerExecutionResult> ExecuteAsync(
            InstallerRequest request,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) => Task.FromResult(EngineSessionFactory.Success);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Read-only inspector returning a configurable snapshot behind the shared gate.</summary>
internal sealed class EngineInspector : IWindowsInstallerParentInspector
{
    private readonly EngineSessionFactory? _gate;

    internal EngineInspector(InstallerRuntimeInspection result, EngineSessionFactory? gate = null)
    {
        Result = result;
        _gate = gate;
    }

    internal InstallerRuntimeInspection Result { get; set; }

    internal int Calls { get; private set; }

    internal List<InstallerRequest> Requests { get; } = [];

    public async Task<InstallerRuntimeInspection> InspectAsync(
        InstallerRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        Calls++;
        Requests.Add(request);
        if (_gate is not null)
        {
            _ = await _gate.RunAsync(EngineStage.Inspect, EngineSessionFactory.Success, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Result;
    }
}

/// <summary>COM activation failure carrying an exact HRESULT without constructing the reserved base type.</summary>
internal sealed class TestComException : System.Runtime.InteropServices.COMException
{
    internal TestComException(string message, int hresult)
        : base(message, hresult)
    {
    }
}

using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

internal static class InstallerPresentationTestData
{
    internal static InstallerRuntimeReadiness Readiness(
        InstallerProductState productState = InstallerProductState.Available,
        InstallerOperation? recoveryOperation = null,
        bool canExecute = true,
        IReadOnlyList<InstallerOperation>? allowedOperations = null,
        IReadOnlyList<InstallerCapabilityStatus>? capabilities = null,
        string diagnosticCode = "installer.runtime.ready") =>
        new(
            canExecute,
            diagnosticCode,
            canExecute ? "可以安装" : "尚未就绪",
            canExecute ? "全部执行前提已经验证。" : "执行前提尚未全部验证。",
            "1.2.3.4",
            productState,
            recoveryOperation,
            allowedOperations ?? DefaultAllowedOperations(
                productState,
                recoveryOperation,
                canExecute),
            capabilities ??
            [
                new InstallerCapabilityStatus(
                    "Windows 11+ x64",
                    canExecute ? "已验证。" : "尚未验证。",
                    canExecute),
            ]);

    private static IReadOnlyList<InstallerOperation> DefaultAllowedOperations(
        InstallerProductState productState,
        InstallerOperation? recoveryOperation,
        bool canExecute)
    {
        if (!canExecute)
        {
            return [];
        }

        return productState switch
        {
            InstallerProductState.Available => [InstallerOperation.Install],
            InstallerProductState.Installed =>
                [InstallerOperation.Repair, InstallerOperation.Uninstall],
            InstallerProductState.RecoveryRequired when recoveryOperation is { } operation =>
                [operation],
            _ => [],
        };
    }

    internal static InstallerExecutionResult Result(
        InstallerExecutionOutcome outcome = InstallerExecutionOutcome.Succeeded,
        bool recoveryPending = false,
        InstallerTransactionPhase? phase = InstallerTransactionPhase.Verified) =>
        new(outcome, "installer.test.result", phase, recoveryPending);
}

internal sealed class ScriptedInstallerRuntime : IInstallerRuntime, IDisposable
{
    internal Func<CancellationToken, Task<InstallerRuntimeReadiness>> Inspect { get; set; } =
        static _ => Task.FromResult(InstallerPresentationTestData.Readiness());

    internal Func<
        InstallerOperation,
        IProgress<InstallerProgress>,
        CancellationToken,
        Task<InstallerExecutionResult>> Execute
    { get; set; } =
        static (_, _, _) => Task.FromResult(InstallerPresentationTestData.Result());

    internal int InspectionCount { get; private set; }

    internal List<InstallerOperation> Operations { get; } = [];

    internal int DisposeCount { get; private set; }

    public Task<InstallerRuntimeReadiness> InspectReadinessAsync(
        CancellationToken cancellationToken)
    {
        InspectionCount++;
        return Inspect(cancellationToken);
    }

    public Task<InstallerExecutionResult> ExecuteAsync(
        InstallerOperation operation,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken)
    {
        Operations.Add(operation);
        return Execute(operation, progress, cancellationToken);
    }

    public void Dispose() => DisposeCount++;
}

internal sealed class QueuedSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _work = [];

    public override void Post(SendOrPostCallback d, object? state) => _work.Enqueue((d, state));

    internal void Drain()
    {
        while (_work.TryDequeue(out (SendOrPostCallback Callback, object? State) work))
        {
            work.Callback(work.State);
        }
    }
}

internal sealed class FatalPresentationTestException : OutOfMemoryException
{
    internal FatalPresentationTestException(string message)
        : base(message)
    {
    }
}

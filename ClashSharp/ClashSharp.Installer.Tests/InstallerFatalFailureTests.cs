using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;

namespace ClashSharp.Installer.Tests;

/// <summary>Installer containment must preserve severe causes beneath ordinary wrappers.</summary>
public sealed class InstallerFatalFailureTests
{
    [Theory]
    [InlineData("aggregate", "memory")]
    [InlineData("aggregate", "stack")]
    [InlineData("aggregate", "access")]
    [InlineData("aggregate", "domain")]
    [InlineData("protocol", "memory")]
    [InlineData("protocol", "stack")]
    [InlineData("protocol", "access")]
    [InlineData("protocol", "domain")]
    [InlineData("cancel", "memory")]
    [InlineData("cancel", "stack")]
    [InlineData("cancel", "access")]
    [InlineData("cancel", "domain")]
    public async Task WrappedFatalDuringInspectionPropagatesWithoutMutations(string wrapper, string fatalKind)
    {
        using CancellationTokenSource caller = new();
        Exception failure = Wrap(wrapper, Fatal(fatalKind), caller.Token);
        InstallerScenario scenario = new()
        {
            EnvironmentAction = _ =>
            {
                if (wrapper == "cancel") { caller.Cancel(); }
                return Task.FromException<InstallerEnvironmentSnapshot>(failure);
            },
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        Assert.Same(failure, await Record.ExceptionAsync(() => coordinator.ExecuteAsync(
            InstallerTestData.Request(), null, caller.Token)));
        Assert.Equal(["environment.inspect"], scenario.Events);
    }

    [Theory]
    [InlineData("aggregate")]
    [InlineData("protocol")]
    [InlineData("cancel")]
    public async Task WrappedFatalAfterDurablePreparationPropagatesAndReleasesExecutionGate(string wrapper)
    {
        using CancellationTokenSource caller = new();
        Exception failure = Wrap(wrapper, Fatal("memory"), caller.Token);
        InstallerScenario scenario = new()
        {
            MachineAction = _ =>
            {
                if (wrapper == "cancel") { caller.Cancel(); }
                return Task.FromException(failure);
            },
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        Assert.Same(failure, await Record.ExceptionAsync(() => coordinator.ExecuteAsync(
            InstallerTestData.Request(), null, caller.Token)));
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
        scenario.MachineAction = null;
        InstallerExecutionResult next = await coordinator.ExecuteAsync(InstallerTestData.Request(), null, CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, next.Outcome);
    }

    internal static Exception Wrap(string kind, Exception cause, CancellationToken token) => kind switch
    {
        "aggregate" => new AggregateException(new IOException("Ordinary sibling"), new InvalidOperationException("Wrapper", cause)),
        "protocol" => new InstallerProtocolException("installer.test.wrapped_failure", cause),
        "cancel" => new OperationCanceledException("Canceled wrapper", cause, token),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

#pragma warning disable CA2201 // Synthetic runtime-fatal exception objects exercise propagation without causing a runtime fault.
    internal static Exception Fatal(string kind) => kind switch
    {
        "memory" => new OutOfMemoryException(),
        "stack" => new StackOverflowException(),
        "access" => new AccessViolationException(),
        "domain" => new AppDomainUnloadedException(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
#pragma warning restore CA2201
}

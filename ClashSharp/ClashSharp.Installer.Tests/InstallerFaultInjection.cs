using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

/// <summary>Failure shapes a boundary can exhibit at an effect point.</summary>
public enum InjectedFault
{
    /// <summary>A stable, deterministic installer protocol failure.</summary>
    Protocol,

    /// <summary>An unexpected platform I/O failure whose text must never surface.</summary>
    Io,

    /// <summary>A privileged participant that cannot prove what it committed.</summary>
    StateUncertain,

    /// <summary>An explicit user decision such as a denied elevation prompt.</summary>
    UserCancelled,

    /// <summary>The caller token is cancelled and the operation observes it by throwing.</summary>
    CancelAndThrow,

    /// <summary>The caller token is cancelled but the operation itself keeps running.</summary>
    CancelOnly,
}

/// <summary>
/// Deterministically decides whether the n-th effect boundary reached by a scenario fails, and
/// records the boundary sequence so a clean run can enumerate every injection point.
/// </summary>
internal sealed class FaultPlan
{
    internal const string ProtocolCode = "installer.test.injected_fault";
    internal const string UncertainCode = "installer.test.injected_uncertain";
    internal const string UserCancelCode = "installer.test.injected_user_cancel";

    private readonly Func<int, string, InjectedFault?> _decide;
    private readonly Func<int> _eventCount;

    internal FaultPlan(Func<int> eventCount, Func<int, string, InjectedFault?>? decide = null)
    {
        _eventCount = eventCount;
        _decide = decide ?? ((_, _) => null);
    }

    internal CancellationTokenSource Cancellation { get; } = new();

    internal List<string> Points { get; } = [];

    internal int? FaultedPoint { get; private set; }

    internal InjectedFault? AppliedFault { get; private set; }

    internal int EventCountAtFault { get; private set; } = -1;

    internal static FaultPlan At(Func<int> eventCount, int point, InjectedFault fault) =>
        new(eventCount, (index, _) => index == point ? fault : null);

    internal async Task Hit(string point, CancellationToken token)
    {
        int index = Points.Count;
        Points.Add(point);
        InjectedFault? fault = _decide(index, point);
        if (fault is null)
        {
            return;
        }

        FaultedPoint = index;
        AppliedFault = fault;
        EventCountAtFault = _eventCount();
        await Task.Yield();
        switch (fault.Value)
        {
            case InjectedFault.Protocol:
                throw new InstallerProtocolException(ProtocolCode);
            case InjectedFault.Io:
                throw new IOException("secret-profile-path-and-token");
            case InjectedFault.StateUncertain:
                throw new InstallerStateUncertainException(UncertainCode);
            case InjectedFault.UserCancelled:
                throw new InstallerUserCancelledException(UserCancelCode);
            case InjectedFault.CancelAndThrow:
                await Cancellation.CancelAsync();
                throw new OperationCanceledException(Cancellation.Token);
            case InjectedFault.CancelOnly:
                await Cancellation.CancelAsync();
                return;
            default:
                throw new InvalidOperationException("Unknown injected fault.");
        }
    }

    /// <summary>Gets the diagnostic code a coordinator must surface for the applied fault.</summary>
    internal string? ExpectedDiagnosticCode => AppliedFault switch
    {
        InjectedFault.Protocol => ProtocolCode,
        InjectedFault.Io => "installer.unexpected_failure",
        InjectedFault.StateUncertain => UncertainCode,
        InjectedFault.UserCancelled => UserCancelCode,
        InjectedFault.CancelAndThrow => "installer.cancelled",
        _ => null,
    };
}

internal static class InstallerScenarioFaultExtensions
{
    /// <summary>Routes every effect hook of the scenario through one fault plan.</summary>
    internal static void Attach(this InstallerScenario scenario, FaultPlan plan)
    {
        InstallerEnvironmentSnapshot environment = scenario.Environment;
        scenario.EnvironmentAction = async token =>
        {
            await plan.Hit("environment", token);
            return environment;
        };
        scenario.ReleaseReverifyAction = token => plan.Hit("release.reverify", token);
        scenario.PackageAction = token => plan.Hit("package.apply", token);
        scenario.CertificateAction = token => plan.Hit("certificate.apply", token);
        scenario.MachineAction = token => plan.Hit("machine.apply", token);
        scenario.MachineResponseAction = token => plan.Hit("machine.apply.reply", token);
        scenario.MachinePrepareAction = token => plan.Hit("machine.prepare", token);
        scenario.MachinePrepareAdmissionAction = token => plan.Hit("machine.prepare.admission", token);
        scenario.PackageCommitAction = token => plan.Hit("machine.commit_package", token);
        scenario.FinalVerifyAction = token => plan.Hit("final.verify", token);
        scenario.FinalClearResponseAction = token => plan.Hit("final.clear.reply", token);
        scenario.Store.LoadAction = async token =>
        {
            await plan.Hit("journal.load", token);
            return scenario.Store.Current;
        };
    }

    /// <summary>Removes all injected behaviour, leaving durable state untouched.</summary>
    internal static void Detach(this InstallerScenario scenario)
    {
        InstallerEnvironmentSnapshot environment = scenario.Environment;
        scenario.EnvironmentAction = _ => Task.FromResult(environment);
        scenario.ReleaseReverifyAction = null;
        scenario.PackageAction = null;
        scenario.CertificateAction = null;
        scenario.MachineAction = null;
        scenario.MachineResponseAction = null;
        scenario.MachinePrepareAction = null;
        scenario.MachinePrepareAdmissionAction = null;
        scenario.PackageCommitAction = null;
        scenario.FinalVerifyAction = null;
        scenario.FinalClearResponseAction = null;
        scenario.Store.LoadAction = null;
    }
}

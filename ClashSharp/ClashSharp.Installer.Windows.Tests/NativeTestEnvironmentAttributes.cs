using System.Security.Principal;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>Describes the machine a native test is running on, so a test that changes machine state can refuse to run there.</summary>
internal static class NativeTestEnvironment
{
    /// <summary>
    /// True on the workflow's disposable GitHub-hosted Windows image or inside Windows Sandbox: the only places where
    /// tests that change certificate trust stores may run.
    /// </summary>
    internal static bool IsDisposableMachine { get; } =
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted"
        || Environment.UserName == "WDAGUtilityAccount"
            && Environment.GetEnvironmentVariable("USERPROFILE") == @"C:\Users\WDAGUtilityAccount";

    /// <summary>True when the process runs elevated, which the native directory tests need to create machine-level roots.</summary>
    internal static bool IsElevated { get; } = ReadElevation();

    private static bool ReadElevation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal const string DisposableMachineReason =
        "Changes certificate trust stores; runs only on disposable hosted CI or in Windows Sandbox.";

    internal const string ElevatedRunnerReason =
        "Needs an elevated Windows runner (it uses only its own test directories, never the product's).";
}

/// <summary>
/// A fact that is skipped, not failed, on a developer machine. CI is a disposable machine and runs it, and the CI
/// evidence step rejects any skipped test, so a broken environment probe cannot silently drop it there.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisposableMachineFactAttribute : FactAttribute
{
    public DisposableMachineFactAttribute()
    {
        if (!NativeTestEnvironment.IsDisposableMachine)
        {
            Skip = NativeTestEnvironment.DisposableMachineReason;
        }
    }
}

/// <summary>The theory counterpart of <see cref="DisposableMachineFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisposableMachineTheoryAttribute : TheoryAttribute
{
    public DisposableMachineTheoryAttribute()
    {
        if (!NativeTestEnvironment.IsDisposableMachine)
        {
            Skip = NativeTestEnvironment.DisposableMachineReason;
        }
    }
}

/// <summary>A fact that is skipped unless the process is elevated; the CI runner is.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ElevatedRunnerFactAttribute : FactAttribute
{
    public ElevatedRunnerFactAttribute()
    {
        if (!NativeTestEnvironment.IsElevated)
        {
            Skip = NativeTestEnvironment.ElevatedRunnerReason;
        }
    }
}

/// <summary>The theory counterpart of <see cref="ElevatedRunnerFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ElevatedRunnerTheoryAttribute : TheoryAttribute
{
    public ElevatedRunnerTheoryAttribute()
    {
        if (!NativeTestEnvironment.IsElevated)
        {
            Skip = NativeTestEnvironment.ElevatedRunnerReason;
        }
    }
}

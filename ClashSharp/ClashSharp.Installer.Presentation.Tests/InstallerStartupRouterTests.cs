using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Presentation;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerStartupRouterTests
{
    private const string TransactionId =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string JournalHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void OrdinaryLaunchCreatesOnlyTheUserInterfaceComposition()
    {
        int helperCalls = 0;
        int userInterfaceCalls = 0;

        int exitCode = InstallerStartupRouter.Run(
            [],
            _ =>
            {
                helperCalls++;
                return 31;
            },
            () =>
            {
                userInterfaceCalls++;
                return 17;
            },
            invalidArgumentsExitCode: 2);

        Assert.Equal(17, exitCode);
        Assert.Equal(0, helperCalls);
        Assert.Equal(1, userInterfaceCalls);
    }

    [Fact]
    public void ValidMachineLaunchCreatesOnlyThePrivilegedComposition()
    {
        InstallerMachineHelperBootstrap expected = InstallerMachineHelperBootstrap.Create(
            new InstallerMachineHelperInvocation(
                InstallerMachineHelperVerb.Prepare,
                TransactionId,
                JournalHash),
            parentProcessId: 4242);
        int helperCalls = 0;
        int userInterfaceCalls = 0;

        int exitCode = InstallerStartupRouter.Run(
            expected.ToArguments(),
            invocation =>
            {
                helperCalls++;
                Assert.Equal(expected, invocation);
                return 19;
            },
            () =>
            {
                userInterfaceCalls++;
                return 17;
            },
            invalidArgumentsExitCode: 2);

        Assert.Equal(19, exitCode);
        Assert.Equal(1, helperCalls);
        Assert.Equal(0, userInterfaceCalls);
    }

    [Fact]
    public void InvalidMachineGrammarFailsClosedBeforeEitherComposition()
    {
        int helperCalls = 0;
        int userInterfaceCalls = 0;

        int exitCode = InstallerStartupRouter.Run(
            ["--machine-helper"],
            _ =>
            {
                helperCalls++;
                return 31;
            },
            () =>
            {
                userInterfaceCalls++;
                return 17;
            },
            invalidArgumentsExitCode: 2);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, helperCalls);
        Assert.Equal(0, userInterfaceCalls);
    }
}

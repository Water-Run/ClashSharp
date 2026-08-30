using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineHelperBootstrapTests
{
    [Fact]
    public void ExactEightArgumentBootstrapRoundTripsWithoutPaths()
    {
        InstallerMachineHelperInvocation invocation = Invocation();
        InstallerMachineHelperBootstrap expected =
            InstallerMachineHelperBootstrap.Create(invocation, parentProcessId: 4242);

        InstallerMachineHelperBootstrap? actual =
            InstallerMachineHelperBootstrap.Parse(expected.ToArguments());

        Assert.Equal(expected, actual);
        Assert.Equal(8, actual!.ToArguments().Count);
        Assert.Equal(invocation, actual.Invocation);
        Assert.Equal(4242, actual.ParentProcessId);
        Assert.DoesNotContain(actual.ToArguments(), static argument =>
            argument.Contains('/') || argument.Contains('\\'));
    }

    [Fact]
    public void OrdinaryArgumentsRemainOutsideTheHelperBootstrap()
    {
        Assert.Null(InstallerMachineHelperBootstrap.Parse([]));
        Assert.Null(InstallerMachineHelperBootstrap.Parse(["--help"]));
        Assert.Null(InstallerMachineHelperBootstrap.Parse(["settings"]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("2147483648")]
    public void ParentPidTextMustBePositiveCanonicalInt32(string parentPid)
    {
        string[] arguments =
            InstallerMachineHelperBootstrap.Create(Invocation(), parentProcessId: 1)
                .ToArguments()
                .ToArray();
        arguments[7] = parentPid;

        AssertDiagnostic(
            () => InstallerMachineHelperBootstrap.Parse(arguments),
            "installer.machine_helper.arguments_invalid");
    }

    [Fact]
    public void MissingReorderedUnknownReservedAndTrailingArgumentsFailClosed()
    {
        IReadOnlyList<string> valid =
            InstallerMachineHelperBootstrap.Create(Invocation(), parentProcessId: 4242)
                .ToArguments();
        string[][] invalid =
        [
            [.. valid.Take(6)],
            [.. valid.Take(6), "--parent-pid", valid[7]],
            [.. valid.Take(6), valid[7], valid[6]],
            [.. valid, "trailing"],
            ["--help", "--machine-parent-pid", "4242"],
            ["--machine-parent-pid", "4242"],
        ];

        foreach (string[] candidate in invalid)
        {
            AssertDiagnostic(
                () => InstallerMachineHelperBootstrap.Parse(candidate),
                "installer.machine_helper.arguments_invalid");
        }

        AssertDiagnostic(
            () => InstallerMachineHelperBootstrap.Parse([.. valid.Take(7), null!]),
            "installer.machine_helper.arguments_invalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InMemoryBootstrapRejectsNonpositiveParentPid(int parentProcessId)
    {
        var bootstrap = new InstallerMachineHelperBootstrap(
            Invocation(),
            parentProcessId);

        AssertDiagnostic(
            bootstrap.Validate,
            "installer.machine_helper.parent_process_invalid");
    }

    private static InstallerMachineHelperInvocation Invocation()
    {
        InstallerRequest request = new(
            InstallerOperation.Install,
            "S-1-5-21-100-200-300-1001",
            AllowReassociation: false,
            "1.2.3.4",
            InstallerTestData.Hash);
        InstallerTransactionSnapshot snapshot = InstallerTransactionSnapshot.Create(
            InstallerTransactionJournal.Create(request));
        return InstallerMachineHelperInvocation.Create(
            InstallerMachineHelperVerb.Prepare,
            snapshot);
    }

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }
}

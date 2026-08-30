using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerProductStatePolicyTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string PayloadHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Theory]
    [InlineData(null, InstallerProductState.Available)]
    [InlineData("1.2.3.4", InstallerProductState.Installed)]
    public void OrdinaryPackageFactsSelectOneIdleState(
        string? installedVersion,
        InstallerProductState expectedState)
    {
        InstallerProductStateDecision decision = InstallerProductStatePolicy.Decide(
            installedVersion,
            durableTransaction: null);

        Assert.Equal(expectedState, decision.ProductState);
        Assert.Null(decision.RecoveryOperation);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.Prepared)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.Verified)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.Prepared)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.Verified)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.Prepared)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.MachineRemovalAuthorized)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.Verified)]
    public void EveryDurablePhaseOverridesOrdinaryMaintenanceWithExactRecovery(
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionSnapshot durable = Transaction(operation, phase);

        InstallerProductStateDecision decision = InstallerProductStatePolicy.Decide(
            operation == InstallerOperation.Uninstall ? null : "1.2.3.4",
            durable);

        Assert.Equal(InstallerProductState.RecoveryRequired, decision.ProductState);
        Assert.Equal(operation, decision.RecoveryOperation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("01.2.3.4")]
    [InlineData("65536.2.3.4")]
    public void NoncanonicalInstalledVersionFailsClosed(string installedVersion)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerProductStatePolicy.Decide(
                installedVersion,
                durableTransaction: null));

        Assert.Equal(
            "installer.request.package_version_invalid",
            exception.DiagnosticCode);
    }

    [Fact]
    public void InvalidDurableSnapshotCannotClaimRecovery()
    {
        InstallerTransactionSnapshot durable = Transaction(InstallerOperation.Repair) with
        {
            ContentHash = PayloadHash,
        };

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerProductStatePolicy.Decide("1.2.3.4", durable));

        Assert.Equal(
            "installer.transaction.content_hash_mismatch",
            exception.DiagnosticCode);
    }

    [Theory]
    [InlineData(InstallerProductState.Available, InstallerOperation.Install)]
    [InlineData(InstallerProductState.Installed, InstallerOperation.Uninstall)]
    [InlineData(InstallerProductState.RecoveryRequired, null)]
    [InlineData(InstallerProductState.RecoveryRequired, (InstallerOperation)999)]
    [InlineData((InstallerProductState)999, null)]
    public void InMemoryDecisionCannotAddDropOrInventARecoveryOperation(
        InstallerProductState productState,
        InstallerOperation? recoveryOperation)
    {
        var decision = new InstallerProductStateDecision(productState, recoveryOperation);

        InstallerProtocolException exception =
            Assert.Throws<InstallerProtocolException>(decision.Validate);
        Assert.Equal(
            "installer.runtime.product_state_invalid",
            exception.DiagnosticCode);
    }

    private static InstallerTransactionSnapshot Transaction(
        InstallerOperation operation,
        InstallerTransactionPhase phase = InstallerTransactionPhase.Prepared)
    {
        InstallerRequest request = new(
            operation,
            TargetSid,
            AllowReassociation: operation == InstallerOperation.Repair,
            "1.2.3.4",
            PayloadHash);
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        InstallerTransactionPhase[] forwardPhases = operation == InstallerOperation.Uninstall
            ? [
                InstallerTransactionPhase.Prepared,
                InstallerTransactionPhase.MachineRemovalAuthorized,
                InstallerTransactionPhase.MachineCommitted,
                InstallerTransactionPhase.PackageCommitted,
                InstallerTransactionPhase.Verified,
            ]
            : [
                InstallerTransactionPhase.Prepared,
                InstallerTransactionPhase.MachineReserved,
                InstallerTransactionPhase.PackageCommitted,
                InstallerTransactionPhase.MachineCommitted,
                InstallerTransactionPhase.Verified,
            ];
        foreach (InstallerTransactionPhase next in forwardPhases.Skip(1))
        {
            if (journal.Phase == phase)
            {
                return InstallerTransactionSnapshot.Create(journal);
            }

            journal = journal.TransitionTo(next);
        }

        if (journal.Phase != phase)
        {
            throw new InvalidOperationException("The test requested an invalid operation phase.");
        }

        return InstallerTransactionSnapshot.Create(journal);
    }
}

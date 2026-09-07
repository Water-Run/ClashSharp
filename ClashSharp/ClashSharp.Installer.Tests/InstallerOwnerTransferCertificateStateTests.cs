using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerOwnerTransferCertificateStateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OnlyCopyBeforeRemovalStatesAreReachable(bool previousPresent, bool nextPresent)
    {
        InstallerOwnerTransferJournal journal = CreateJournal(previousPresent, nextPresent);
        InstallerCertificateOwnershipLedger? previous = journal.PreviousCertificateLedger;
        InstallerCertificateOwnershipLedger? next = journal.NextCertificateLedger;
        var initial = new InstallerOwnerTransferCertificateState(previous, null, next);
        var preserved = new InstallerOwnerTransferCertificateState(previous, previous, next);
        var activated = new InstallerOwnerTransferCertificateState(next, previous, next);
        var completed = new InstallerOwnerTransferCertificateState(next, previous, null);
        var expected = new Dictionary<InstallerOwnerTransferCertificateState, InstallerOwnerTransferCertificateStep?>
        {
            [completed] = null,
        };
        if (nextPresent)
        {
            expected[activated] = new(InstallerOwnerTransferCertificateAction.ReleaseActivatedArchive, completed);
        }
        if (previousPresent || nextPresent)
        {
            expected[preserved] = new(InstallerOwnerTransferCertificateAction.ActivateNext, activated);
        }
        if (previousPresent)
        {
            expected[initial] = new(InstallerOwnerTransferCertificateAction.PreservePrevious, preserved);
        }

        InstallerCertificateOwnershipLedger?[] values = [null, previous, next,
            InstallerTestData.CertificateLedger() with { LedgerId = new string('f', 64) }];
        foreach (var active in values.Distinct())
        {
            foreach (var previousArchive in values.Distinct())
            {
                foreach (var nextArchive in values.Distinct())
                {
                    var state = new InstallerOwnerTransferCertificateState(active, previousArchive, nextArchive);
                    if (expected.TryGetValue(state, out InstallerOwnerTransferCertificateStep? step))
                    {
                        Assert.Equal(step, state.GetNextStep(journal));
                    }
                    else
                    {
                        InstallerProtocolException error = Assert.Throws<InstallerProtocolException>(() => state.GetNextStep(journal));
                        Assert.Equal("installer.owner_transfer.certificate_state_conflict", error.DiagnosticCode);
                        Assert.Null(error.InnerException);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.Prepared)]
    [InlineData(InstallerOwnerTransferPhase.StartupBlocked)]
    [InlineData(InstallerOwnerTransferPhase.PreviousServiceRemoved)]
    [InlineData(InstallerOwnerTransferPhase.MachineAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.CertificateStateTransferred)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.Verified)]
    public void OtherDurablePhasesCannotSelectCertificateMutations(InstallerOwnerTransferPhase phase)
    {
        var journal = CreateJournal(true, true) with { Phase = phase, Generation = (int)phase + 1 };
        var state = new InstallerOwnerTransferCertificateState(journal.PreviousCertificateLedger, null, journal.NextCertificateLedger);

        InstallerProtocolException error = Assert.Throws<InstallerProtocolException>(() => state.GetNextStep(journal));

        Assert.Equal("installer.owner_transfer.certificate_phase_invalid", error.DiagnosticCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferencesAndCertificateOwnershipRemainExactAcrossEveryBoundary(bool previousWasPreExisting)
    {
        var journal = CreateJournal(true, true);
        var previous = journal.PreviousCertificateLedger! with
        {
            WasPreExisting = previousWasPreExisting,
            InstallerOwned = !previousWasPreExisting,
        };
        if (previousWasPreExisting)
        {
            previous = previous.TakeOwnershipForMissingCertificate();
        }
        journal = journal with { PreviousCertificateLedger = previous };
        var state = new InstallerOwnerTransferCertificateState(previous, null, journal.NextCertificateLedger);
        byte[] previousBytes = InstallerCertificateOwnershipCodec.Serialize(previous);
        byte[] nextBytes = InstallerCertificateOwnershipCodec.Serialize(journal.NextCertificateLedger!);
        int boundaries = 0;
        while (state.GetNextStep(journal) is { } step)
        {
            state = step.After;
            Assert.True(++boundaries <= 3);
            Assert.Contains(previous, new[] { state.Active, state.PreviousArchive });
            Assert.Contains(journal.NextCertificateLedger, new[] { state.Active, state.NextArchive });
        }

        Assert.Equal(3, boundaries);
        Assert.Equal(previousBytes, InstallerCertificateOwnershipCodec.Serialize(state.PreviousArchive!));
        Assert.Equal(nextBytes, InstallerCertificateOwnershipCodec.Serialize(state.Active!));
        Assert.Null(state.NextArchive);
        Assert.Null(state.GetNextStep(journal));
    }

    [Theory]
    [InlineData("released")]
    [InlineData("generation")]
    [InlineData("owner")]
    [InlineData("certificate")]
    public void RewrittenOrReleasedLiveEvidenceIsNeverAccepted(string changed)
    {
        var journal = CreateJournal(true, true);
        var previous = journal.PreviousCertificateLedger!;
        var altered = changed switch
        {
            "released" => previous.PrepareRemoval(),
            "generation" => previous with { Generation = 2 },
            "owner" => previous with { TargetSid = journal.NextOwner.Association.OwnerSid },
            _ => previous with { CertificateSha256 = new string('f', 64) },
        };
        var state = new InstallerOwnerTransferCertificateState(previous, altered, journal.NextCertificateLedger);

        Assert.Throws<InstallerProtocolException>(() => state.GetNextStep(journal));
        Assert.DoesNotContain(previous.TargetSid, state.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(previous.CertificateSha256, state.ToString(), StringComparison.Ordinal);
    }

    private static InstallerOwnerTransferJournal CreateJournal(bool previous, bool next)
    {
        var journal = InstallerOwnerTransferJournalTests.CreateJournal(withLedgers: true);
        return journal with
        {
            Phase = InstallerOwnerTransferPhase.AssociationTransferred,
            Generation = (int)InstallerOwnerTransferPhase.AssociationTransferred + 1,
            PreviousCertificateLedger = previous ? journal.PreviousCertificateLedger : null,
            NextCertificateLedger = next ? journal.NextCertificateLedger : null,
        };
    }
}

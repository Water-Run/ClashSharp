using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// Certificate ownership as a state space: the ledger's valid states are exactly those reachable by its
/// own transitions, and the owner-transfer three-slot policy accepts exactly the crash-reachable states.
/// </summary>
public sealed class InstallerCertificateStatePropertyTests
{
    [Fact]
    public void LedgerValidationAcceptsExactlyTheStatesReachableByItsTransitions()
    {
        InstallerCertificateOwnershipLedger owned = InstallerTestData.CertificateLedger();
        InstallerCertificateOwnershipLedger preExisting = InstallerTestData.CertificateLedger(wasPreExisting: true);
        HashSet<InstallerCertificateOwnershipLedger> reachable = [];
        Queue<InstallerCertificateOwnershipLedger> frontier = new([owned, preExisting]);
        while (frontier.TryDequeue(out InstallerCertificateOwnershipLedger? state))
        {
            if (!reachable.Add(state))
            {
                continue;
            }

            foreach (InstallerCertificateOwnershipLedger next in new[] { state.PrepareRemoval(), TryOwnership(state) }.OfType<InstallerCertificateOwnershipLedger>())
            {
                frontier.Enqueue(next);
            }
        }

        int accepted = 0;
        foreach (int generation in Enumerable.Range(-1, 7))
        {
            foreach (int references in new[] { -1, 0, 1, 2 })
            {
                foreach (bool pre in new[] { false, true })
                {
                    foreach (bool owner in new[] { false, true })
                    {
                        InstallerCertificateOwnershipLedger candidate = owned with
                        {
                            Generation = generation,
                            ManagedReferenceCount = references,
                            WasPreExisting = pre,
                            InstallerOwned = owner,
                        };
                        bool valid = Accepts(candidate.Validate);

                        Assert.Equal(reachable.Contains(candidate), valid);
                        accepted += valid ? 1 : 0;
                    }
                }
            }
        }

        Assert.Equal(reachable.Count, accepted);
        Assert.Equal(6, reachable.Count);
    }

    [Fact]
    public void LedgerTransitionsAreIdempotentMonotonicAndNeverLeaveTheValidSet()
    {
        foreach (bool pre in new[] { false, true })
        {
            InstallerCertificateOwnershipLedger start = InstallerTestData.CertificateLedger(wasPreExisting: pre);
            InstallerCertificateOwnershipLedger released = start.PrepareRemoval();

            released.Validate();
            Assert.Equal(0, released.ManagedReferenceCount);
            Assert.Equal(start.Generation + 1, released.Generation);
            Assert.Equal(start.LedgerId, released.LedgerId);
            Assert.Equal(released, released.PrepareRemoval());
            Assert.Equal(start.WasPreExisting, released.WasPreExisting);

            InstallerCertificateOwnershipLedger? taken = TryOwnership(start);
            if (pre)
            {
                Assert.NotNull(taken);
                taken.Validate();
                Assert.True(taken.InstallerOwned && !taken.WasPreExisting);
                Assert.Equal(taken, taken.TakeOwnershipForMissingCertificate());
                Assert.Equal(start.LedgerId, taken.LedgerId);
                // Ownership of a certificate we did not import can only be taken before it was released.
                Assert.Throws<InstallerProtocolException>(released.TakeOwnershipForMissingCertificate);
                taken.PrepareRemoval().Validate();
            }
            else
            {
                Assert.Same(start, start.TakeOwnershipForMissingCertificate());
            }
        }
    }

    [Fact]
    public void LedgerMatchingBindsTheAccountAndTheExactCertificateIdentity()
    {
        InstallerCertificateOwnershipLedger ledger = InstallerTestData.CertificateLedger();
        InstallerRequest request = InstallerTestData.Request();
        VerifiedInstallerRelease release = InstallerTestData.Release();

        Assert.True(ledger.Matches(request, release));
        Assert.False(ledger.Matches(request with { TargetSid = "S-1-5-21-100-200-300-1002" }, release));
        Assert.False(ledger.Matches(request, release with { PackageCertificateThumbprint = InstallerTestData.AuthenticodeThumbprint }));
        Assert.False(ledger.Matches(request, release with { CertificateSha256 = InstallerTestData.OtherHash }));
        Assert.True(ledger.Matches(request with { ExpectedPackageVersion = "9.9.9.9", InstallerPayloadSha256 = InstallerTestData.OtherHash },
            release with { ExpectedPackageVersion = "9.9.9.9", InstallerPayloadSha256 = InstallerTestData.OtherHash }));
        Assert.Throws<ArgumentNullException>(() => ledger.Matches(null!, release));
        Assert.Throws<ArgumentNullException>(() => ledger.Matches(request, null!));
        Assert.Throws<InstallerProtocolException>(() => InstallerCertificateOwnershipLedger.Create(
            InstallerTestData.Request(InstallerOperation.Uninstall), release, certificateWasPresent: false));
        Assert.Throws<InstallerProtocolException>(() => InstallerCertificateOwnershipLedger.Create(
            request, InstallerTestData.Release(certificatePayloadAvailable: false), certificateWasPresent: false));
        InstallerCertificateOwnershipLedger present = InstallerCertificateOwnershipLedger.Create(
            request, InstallerTestData.Release(certificatePayloadAvailable: false), certificateWasPresent: true);
        Assert.True(present.WasPreExisting && !present.InstallerOwned);
        Assert.Equal(64, present.LedgerId.Length);
        Assert.NotEqual(
            InstallerCertificateOwnershipLedger.Create(request, release, false).LedgerId,
            InstallerCertificateOwnershipLedger.Create(request, release, false).LedgerId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ThreeSlotPolicyAcceptsExactlyTheCrashReachableStatesAndConvergesWithoutLosingEvidence(
        bool hasPrevious,
        bool hasNext)
    {
        InstallerOwnerTransferJournal journal = CreateJournal(hasPrevious, hasNext);
        InstallerCertificateOwnershipLedger? previous = journal.PreviousCertificateLedger;
        InstallerCertificateOwnershipLedger? next = journal.NextCertificateLedger;
        InstallerCertificateOwnershipLedger foreign = InstallerTestData.CertificateLedger(ledgerId: InstallerTestData.OtherHash);
        InstallerCertificateOwnershipLedger?[] slotValues =
            [.. new InstallerCertificateOwnershipLedger?[] { null, previous, next, foreign, previous?.PrepareRemoval() }.Distinct()];

        // States a crash can leave: before preserving, after preserving, after activating, after releasing.
        HashSet<InstallerOwnerTransferCertificateState> reachable =
        [
            new(previous, null, next),
            new(previous, previous, next),
            new(next, previous, next),
            new(next, previous, null),
        ];

        HashSet<InstallerOwnerTransferCertificateState> accepted = [];
        foreach (InstallerCertificateOwnershipLedger? active in slotValues)
        {
            foreach (InstallerCertificateOwnershipLedger? previousArchive in slotValues)
            {
                foreach (InstallerCertificateOwnershipLedger? nextArchive in slotValues)
                {
                    InstallerOwnerTransferCertificateState state = new(active, previousArchive, nextArchive);
                    InstallerOwnerTransferCertificateStep? step = null;
                    bool valid = Accepts(() => step = state.GetNextStep(journal));

                    Assert.Equal(reachable.Contains(state), valid);
                    if (!valid)
                    {
                        continue;
                    }

                    accepted.Add(state);
                    // Walk the policy to completion: every intermediate state is itself accepted, the walk
                    // is bounded, and no ledger the journal knows about is ever dropped from all three slots.
                    InstallerOwnerTransferCertificateState current = state;
                    for (int moves = 0; ; moves++)
                    {
                        Assert.InRange(moves, 0, 3);
                        InstallerOwnerTransferCertificateStep? following = current.GetNextStep(journal);
                        AssertEvidencePreserved(current, previous, next);
                        if (following is null)
                        {
                            Assert.Equal(new InstallerOwnerTransferCertificateState(next, previous, null), current);
                            break;
                        }

                        Assert.NotEqual(current, following.After);
                        current = following.After;
                    }
                }
            }
        }

        Assert.True(accepted.SetEquals(reachable));
    }

    [Fact]
    public void ThreeSlotPolicyOnlyRunsAtTheAssociationBoundary()
    {
        InstallerOwnerTransferJournal journal = CreateJournal(true, true);
        InstallerOwnerTransferCertificateState state = new(journal.PreviousCertificateLedger, null, journal.NextCertificateLedger);

        foreach (InstallerOwnerTransferPhase phase in Enum.GetValues<InstallerOwnerTransferPhase>())
        {
            InstallerOwnerTransferJournal at = journal with { Phase = phase, Generation = (int)phase + 1 };
            if (phase == InstallerOwnerTransferPhase.AssociationTransferred)
            {
                Assert.NotNull(state.GetNextStep(at));
            }
            else
            {
                Assert.Equal(
                    "installer.owner_transfer.certificate_phase_invalid",
                    Assert.Throws<InstallerProtocolException>(() => state.GetNextStep(at)).DiagnosticCode);
            }
        }

        Assert.Throws<ArgumentNullException>(() => state.GetNextStep(null!));
    }

    private static void AssertEvidencePreserved(
        InstallerOwnerTransferCertificateState state,
        InstallerCertificateOwnershipLedger? previous,
        InstallerCertificateOwnershipLedger? next)
    {
        InstallerCertificateOwnershipLedger?[] slots = [state.Active, state.PreviousArchive, state.NextArchive];
        foreach (InstallerCertificateOwnershipLedger? known in new[] { previous, next }.Where(static ledger => ledger is not null))
        {
            Assert.Contains(known, slots);
        }
    }

    private static InstallerOwnerTransferJournal CreateJournal(bool previous, bool next)
    {
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.CreateJournal(withLedgers: true);
        return journal with
        {
            Phase = InstallerOwnerTransferPhase.AssociationTransferred,
            Generation = (int)InstallerOwnerTransferPhase.AssociationTransferred + 1,
            PreviousCertificateLedger = previous ? journal.PreviousCertificateLedger : null,
            NextCertificateLedger = next ? journal.NextCertificateLedger : null,
        };
    }

    private static InstallerCertificateOwnershipLedger? TryOwnership(InstallerCertificateOwnershipLedger state)
    {
        try
        {
            return state.TakeOwnershipForMissingCertificate();
        }
        catch (InstallerProtocolException)
        {
            return null;
        }
    }

    private static bool Accepts(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (InstallerProtocolException)
        {
            return false;
        }
    }
}

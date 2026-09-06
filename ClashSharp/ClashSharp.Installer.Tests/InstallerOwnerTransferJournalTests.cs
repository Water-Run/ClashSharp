using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerOwnerTransferJournalTests
{
    internal const string PreviousSid = "S-1-5-21-100-200-300-1002";

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public void CreationRetainsExactOrdinaryContinuationAndIndependentCertificateOwners(InstallerOperation operation)
    {
        InstallerOwnerTransferJournal journal = CreateJournal(operation, withLedgers: true);

        journal.Validate();
        Assert.Equal(InstallerOwnerTransferPhase.Prepared, journal.Phase);
        Assert.Equal(1, journal.Generation);
        Assert.Equal(operation, journal.Continuation.Operation);
        Assert.Equal(InstallerTransactionPhase.Prepared, journal.Continuation.Phase);
        Assert.False(journal.Continuation.AllowReassociation);
        Assert.Equal(PreviousSid, journal.PreviousCertificateLedger!.TargetSid);
        Assert.Equal(InstallerTestData.Sid, journal.NextCertificateLedger!.TargetSid);
        Assert.NotEqual(journal.PreviousOwner.Association.AuthenticationToken, journal.NextOwner.Association.AuthenticationToken);
    }

    [Theory]
    [MemberData(nameof(PhasePairs))]
    public void OnlyAdjacentBoundariesAndExactReplaysCanAdvance(
        InstallerOwnerTransferPhase currentPhase,
        InstallerOwnerTransferPhase nextPhase)
    {
        InstallerOwnerTransferJournal journal = AtPhase(currentPhase);
        InstallerOwnerTransferSnapshot current = InstallerOwnerTransferSnapshot.Create(journal);
        InstallerOwnerTransferJournal incoming = journal with { Phase = nextPhase, Generation = (int)nextPhase + 1 };

        if (nextPhase == currentPhase || (int)nextPhase == (int)currentPhase + 1)
        {
            InstallerOwnerTransferStatePolicy.ValidateSave(incoming, current, current.ContentHash);
            Assert.Equal(incoming, journal.TransitionTo(nextPhase));
            Assert.True(journal.HasSameIdentity(incoming));
            if (nextPhase == currentPhase)
            {
                Assert.Same(journal, journal.TransitionTo(nextPhase));
            }
        }
        else
        {
            AssertDiagnostic(
                () => InstallerOwnerTransferStatePolicy.ValidateSave(incoming, current, current.ContentHash),
                "installer.owner_transfer.phase_transition_invalid");
        }
    }

    [Theory]
    [MemberData(nameof(AllPhases))]
    public void OnlyPreparedCanBeCreatedAndOnlyVerifiedCanBeCleared(InstallerOwnerTransferPhase phase)
    {
        InstallerOwnerTransferJournal journal = AtPhase(phase);
        InstallerOwnerTransferSnapshot snapshot = InstallerOwnerTransferSnapshot.Create(journal);

        if (phase == InstallerOwnerTransferPhase.Prepared)
        {
            InstallerOwnerTransferStatePolicy.ValidateSave(journal, null, null);
        }
        else
        {
            AssertDiagnostic(
                () => InstallerOwnerTransferStatePolicy.ValidateSave(journal, null, null),
                "installer.owner_transfer.write_conflict");
        }

        if (phase == InstallerOwnerTransferPhase.Verified)
        {
            InstallerOwnerTransferStatePolicy.ValidateClear(snapshot, journal.Continuation.TransactionId, snapshot.ContentHash);
        }
        else
        {
            AssertDiagnostic(
                () => InstallerOwnerTransferStatePolicy.ValidateClear(snapshot, journal.Continuation.TransactionId, snapshot.ContentHash),
                "installer.owner_transfer.clear_conflict");
        }
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("version")]
    [InlineData("payload")]
    [InlineData("operation")]
    [InlineData("previous-token")]
    [InlineData("next-token")]
    [InlineData("previous-profile")]
    [InlineData("next-profile")]
    [InlineData("previous-certificate")]
    [InlineData("next-certificate")]
    public void EveryPrivateIdentityFieldIsBoundAcrossProgress(string changedField)
    {
        InstallerOwnerTransferJournal journal = CreateJournal(withLedgers: true);
        InstallerOwnerTransferSnapshot current = InstallerOwnerTransferSnapshot.Create(journal);
        InstallerOwnerTransferJournal changed = changedField switch
        {
            "transaction" => journal with { Continuation = journal.Continuation with { TransactionId = InstallerTestData.OtherHash } },
            "version" => journal with { Continuation = journal.Continuation with { ExpectedPackageVersion = "2.0.0.0" } },
            "payload" => journal with { Continuation = journal.Continuation with { InstallerPayloadSha256 = InstallerTestData.OtherHash } },
            "operation" => journal with { Continuation = journal.Continuation with { Operation = InstallerOperation.Repair } },
            "previous-token" => journal with
            {
                PreviousOwner = journal.PreviousOwner with
                {
                    Association = journal.PreviousOwner.Association with { AuthenticationToken = new string('a', 64) },
                },
            },
            "next-token" => journal with
            {
                NextOwner = journal.NextOwner with
                {
                    Association = journal.NextOwner.Association with { AuthenticationToken = new string('a', 64) },
                },
            },
            "previous-profile" => journal with { PreviousOwner = journal.PreviousOwner with { ProfileRoot = @"D:\Users\previous" } },
            "next-profile" => journal with { NextOwner = journal.NextOwner with { ProfileRoot = @"D:\Users\target" } },
            "previous-certificate" => journal with { PreviousCertificateLedger = null },
            "next-certificate" => journal with { NextCertificateLedger = null },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };
        changed = changed.TransitionTo(InstallerOwnerTransferPhase.StartupBlocked);

        Assert.False(journal.HasSameIdentity(changed));
        AssertDiagnostic(
            () => InstallerOwnerTransferStatePolicy.ValidateSave(changed, current, current.ContentHash),
            "installer.owner_transfer.write_conflict");
    }

    [Theory]
    [InlineData("same-owner")]
    [InlineData("wrong-target")]
    [InlineData("same-token")]
    [InlineData("same-profile")]
    public void AmbiguousParticipantsAreRejected(string conflict)
    {
        InstallerOwnerTransferJournal journal = CreateJournal();
        journal = conflict switch
        {
            "same-owner" => journal with
            {
                PreviousOwner = journal.PreviousOwner with
                {
                    Association = journal.PreviousOwner.Association with { OwnerSid = InstallerTestData.Sid },
                },
            },
            "wrong-target" => journal with { Continuation = journal.Continuation with { TargetSid = PreviousSid } },
            "same-token" => journal with
            {
                NextOwner = journal.NextOwner with
                {
                    Association = journal.NextOwner.Association with { AuthenticationToken = InstallerTestData.Hash },
                },
            },
            "same-profile" => journal with { NextOwner = journal.NextOwner with { ProfileRoot = @"c:\USERS\PREVIOUS" } },
            _ => throw new ArgumentOutOfRangeException(nameof(conflict)),
        };

        AssertDiagnostic(journal.Validate, "installer.owner_transfer.identity_conflict");
    }

    [Theory]
    [InlineData("uninstall")]
    [InlineData("takeover-flag")]
    [InlineData("advanced-continuation")]
    public void ContinuationMustRemainAnOrdinaryPreparedInstallOrRepair(string condition)
    {
        InstallerOwnerTransferJournal journal = CreateJournal();
        InstallerTransactionJournal continuation = condition switch
        {
            "uninstall" => journal.Continuation with { Operation = InstallerOperation.Uninstall },
            "takeover-flag" => journal.Continuation with { Operation = InstallerOperation.Repair, AllowReassociation = true },
            "advanced-continuation" => journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        AssertDiagnostic(
            (journal with { Continuation = continuation }).Validate,
            "installer.owner_transfer.continuation_invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CertificateEvidenceCannotBeAssignedToTheOtherAccount(bool previous)
    {
        InstallerOwnerTransferJournal journal = CreateJournal(withLedgers: true);
        journal = previous
            ? journal with { PreviousCertificateLedger = journal.NextCertificateLedger }
            : journal with { NextCertificateLedger = journal.PreviousCertificateLedger };

        AssertDiagnostic(journal.Validate, "installer.owner_transfer.certificate_identity_invalid");
    }

    [Fact]
    public void ReleasedCertificateReferencesCannotBePresentedAsAnActiveOwnerLedger()
    {
        InstallerOwnerTransferJournal journal = CreateJournal(withLedgers: true);
        journal = journal with { PreviousCertificateLedger = journal.PreviousCertificateLedger!.PrepareRemoval() };

        AssertDiagnostic(journal.Validate, "installer.owner_transfer.certificate_identity_invalid");
    }

    [Fact]
    public void StaleOrMissingCompareAndSwapStateCannotAdvanceOrClear()
    {
        InstallerOwnerTransferJournal journal = CreateJournal();
        InstallerOwnerTransferSnapshot current = InstallerOwnerTransferSnapshot.Create(journal);
        InstallerOwnerTransferJournal next = journal.TransitionTo(InstallerOwnerTransferPhase.StartupBlocked);

        AssertDiagnostic(() => InstallerOwnerTransferStatePolicy.ValidateSave(next, current, InstallerTestData.OtherHash), "installer.owner_transfer.write_conflict");
        AssertDiagnostic(() => InstallerOwnerTransferStatePolicy.ValidateSave(next, current, null), "installer.owner_transfer.write_conflict");
        AssertDiagnostic(() => InstallerOwnerTransferStatePolicy.ValidateSave(journal, null, current.ContentHash), "installer.owner_transfer.write_conflict");
        AssertDiagnostic(() => InstallerOwnerTransferStatePolicy.ValidateClear(null, journal.Continuation.TransactionId, current.ContentHash), "installer.owner_transfer.clear_conflict");

        InstallerOwnerTransferSnapshot verified = InstallerOwnerTransferSnapshot.Create(AtPhase(InstallerOwnerTransferPhase.Verified));
        AssertDiagnostic(() => InstallerOwnerTransferStatePolicy.ValidateClear(verified, InstallerTestData.OtherHash, verified.ContentHash), "installer.owner_transfer.clear_conflict");
        AssertDiagnostic(() => InstallerOwnerTransferStatePolicy.ValidateClear(verified, verified.Journal.Continuation.TransactionId, InstallerTestData.OtherHash), "installer.owner_transfer.clear_conflict");
    }

    [Fact]
    public void InvalidSchemaPhaseGenerationAndDigestAreRejected()
    {
        InstallerOwnerTransferJournal journal = CreateJournal();
        AssertDiagnostic((journal with { Schema = 2 }).Validate, "installer.owner_transfer.schema_invalid");
        AssertDiagnostic((journal with { Phase = (InstallerOwnerTransferPhase)8 }).Validate, "installer.owner_transfer.phase_invalid");
        AssertDiagnostic((journal with { Generation = 2 }).Validate, "installer.owner_transfer.generation_invalid");
        AssertDiagnostic(() => journal.TransitionTo((InstallerOwnerTransferPhase)(-1)), "installer.owner_transfer.phase_transition_invalid");
        InstallerOwnerTransferSnapshot snapshot = InstallerOwnerTransferSnapshot.Create(journal);
        snapshot.Validate();
        AssertDiagnostic((snapshot with { ContentHash = InstallerTestData.OtherHash }).Validate, "installer.owner_transfer.content_hash_mismatch");
        AssertDiagnostic((snapshot with { ContentHash = "invalid" }).Validate, "installer.owner_transfer.content_hash_invalid");
    }

    [Theory]
    [InlineData(@"relative\profile")]
    [InlineData(@"C:profile")]
    [InlineData(@"C:\")]
    [InlineData(@"\\host\share\profile")]
    [InlineData(@"\\?\C:\Users\profile")]
    [InlineData(@"C:\Users\\profile")]
    [InlineData(@"C:\Users\..\profile")]
    [InlineData(@"C:\Users\.\profile")]
    [InlineData(@"C:\Users\profile\")]
    [InlineData(@"C:\Users\profile.")]
    [InlineData(@"C:\Users\profile ")]
    [InlineData(@"C:\Users\profile:stream")]
    [InlineData(@"C:/Users/profile")]
    [InlineData(@"C:\Users\CON")]
    [InlineData(@"C:\Users\nul.txt")]
    [InlineData(@"C:\Users\COM1")]
    [InlineData(@"C:\Users\LPT9.txt")]
    [InlineData(@"C:\Users\COM¹")]
    [InlineData("C:\\Users\\bad\nprofile")]
    public void NoncanonicalOrDeviceProfilePathsAreRejected(string path)
    {
        InstallerOwnerTransferParticipant participant = CreateJournal().PreviousOwner with { ProfileRoot = path };
        AssertDiagnostic(participant.Validate, "installer.owner_transfer.profile_path_invalid");
    }

    [Theory]
    [InlineData(@"C:\Users\ordinary profile")]
    [InlineData(@"d:\用户\测试")]
    [InlineData(@"C:\Users\COM10")]
    [InlineData(@"C:\Users\company.name")]
    [InlineData("C:\\Users\\profile-\U0001F642")]
    public void OrdinaryUnicodeAndSpaceContainingProfilesRemainSupported(string path) =>
        (CreateJournal().PreviousOwner with { ProfileRoot = path }).Validate();

    [Fact]
    public void OversizeAndUnpairedSurrogateProfilesCannotBecomeDifferentSerializedIdentities()
    {
        InstallerOwnerTransferParticipant participant = CreateJournal().PreviousOwner;
        string[] invalid =
        [
            @"C:\" + new string('a', InstallerOwnerTransferParticipant.MaximumProfilePathCharacters),
            @"C:\Users\" + (char)0xD800,
            @"C:\Users\" + (char)0xD800 + "suffix",
            @"C:\Users\" + (char)0xDC00,
        ];
        foreach (string path in invalid)
        {
            AssertDiagnostic(
                (participant with { ProfileRoot = path }).Validate,
                "installer.owner_transfer.profile_path_invalid");
        }
    }

    [Fact]
    public void MissingEvidenceCannotBecomeAnEmptyButValidSnapshot()
    {
        InstallerOwnerTransferJournal journal = CreateJournal();
        AssertDiagnostic((journal with { Continuation = null! }).Validate, "installer.owner_transfer.identity_missing");
        AssertDiagnostic((journal with { PreviousOwner = null! }).Validate, "installer.owner_transfer.identity_missing");
        AssertDiagnostic((journal with { NextOwner = null! }).Validate, "installer.owner_transfer.identity_missing");
        AssertDiagnostic((journal.PreviousOwner with { Association = null! }).Validate, "installer.owner_transfer.participant_invalid");
        AssertDiagnostic((journal.PreviousOwner with { ProfileRoot = null! }).Validate, "installer.owner_transfer.profile_path_invalid");
        AssertDiagnostic(new InstallerOwnerTransferSnapshot(null!, InstallerTestData.Hash).Validate, "installer.owner_transfer.snapshot_invalid");
    }

    [Fact]
    public void ParticipantAndSnapshotDescriptionsNeverIncludePrivateEvidence()
    {
        InstallerOwnerTransferJournal journal = CreateJournal(withLedgers: true);
        string descriptions = string.Join(' ', journal, journal.PreviousOwner, journal.NextOwner, InstallerOwnerTransferSnapshot.Create(journal));

        Assert.DoesNotContain(InstallerTestData.Hash, descriptions, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallerTestData.OtherHash, descriptions, StringComparison.Ordinal);
        Assert.DoesNotContain(PreviousSid, descriptions, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallerTestData.Sid, descriptions, StringComparison.Ordinal);
        Assert.DoesNotContain(journal.PreviousOwner.ProfileRoot, descriptions, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallerTestData.CertificateThumbprint, descriptions, StringComparison.Ordinal);
    }

    public static TheoryData<InstallerOwnerTransferPhase, InstallerOwnerTransferPhase> PhasePairs()
    {
        var cases = new TheoryData<InstallerOwnerTransferPhase, InstallerOwnerTransferPhase>();
        foreach (InstallerOwnerTransferPhase current in Enum.GetValues<InstallerOwnerTransferPhase>())
        {
            foreach (InstallerOwnerTransferPhase next in Enum.GetValues<InstallerOwnerTransferPhase>())
            {
                cases.Add(current, next);
            }
        }

        return cases;
    }

    public static TheoryData<InstallerOwnerTransferPhase> AllPhases() =>
        new(Enum.GetValues<InstallerOwnerTransferPhase>());

    internal static InstallerOwnerTransferJournal AtPhase(InstallerOwnerTransferPhase phase) =>
        CreateJournal() with { Phase = phase, Generation = (int)phase + 1 };

    internal static InstallerOwnerTransferJournal CreateJournal(
        InstallerOperation operation = InstallerOperation.Install,
        bool withLedgers = false)
    {
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournal.Create(
            InstallerTestData.Request(operation),
            new(InstallerMachineAssociation.Create(PreviousSid, InstallerTestData.Hash), @"C:\Users\previous"),
            new(InstallerMachineAssociation.Create(InstallerTestData.Sid, InstallerTestData.OtherHash), @"C:\Users\target"),
            withLedgers ? InstallerTestData.CertificateLedger() with { TargetSid = PreviousSid } : null,
            withLedgers ? InstallerTestData.CertificateLedger() : null);
        return journal with { Continuation = journal.Continuation with { TransactionId = InstallerTestData.TransactionId } };
    }

    private static void AssertDiagnostic(Action action, string expected) =>
        Assert.Equal(expected, Assert.Throws<InstallerProtocolException>(action).DiagnosticCode);
}

extern alias RecoveryWatchdog;

using Dword = RecoveryWatchdog::ClashSharp.Service.WindowsProxyDwordValue;
using IJournalStore = RecoveryWatchdog::ClashSharp.Service.IWindowsProxyMutationJournalStore;
using IRegistryStore = RecoveryWatchdog::ClashSharp.Service.IWindowsProxyRegistryStore;
using Journal = RecoveryWatchdog::ClashSharp.Service.WindowsProxyMutationJournal;
using Phase = RecoveryWatchdog::ClashSharp.Service.WindowsProxyMutationPhase;
using Restorer = RecoveryWatchdog::ClashSharp.Service.WindowsProxyOwnershipRestorer;
using Snapshot = RecoveryWatchdog::ClashSharp.Service.WindowsProxyRegistrySnapshot;
using StringKind = RecoveryWatchdog::ClashSharp.Service.WindowsProxyStringKind;
using StringValue = RecoveryWatchdog::ClashSharp.Service.WindowsProxyStringValue;
using Watchdog = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogRunner;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Contract of the watchdog's proxy restore: it may only undo fields that the durable journal proves
/// Clash# wrote, per field, and every step failure must leave ownership evidence for a later retry.
/// </summary>
public sealed class WatchdogOwnershipRestoreContractTests
{
    private static readonly StringValue Absent = new(false, null, StringKind.None);

    private static StringValue Text(string value) => new(true, value, StringKind.String);

    private static StringValue Expand(string value) => new(true, value, StringKind.ExpandString);

    private static Dword Dw(int value) => new(true, value);

    private static readonly Dword NoDword = new(false, 0);

    /// <summary>Gets the evidence an observer can have for one string field.</summary>
    private static StringValue[] StringDomain =>
    [
        Absent,
        Text("A"),
        Text("B"),
        Text("C"),
        Expand("A"),
        Text(string.Empty),
    ];

    private static Dword[] DwordDomain => [NoDword, Dw(0), Dw(1), Dw(2)];

    [Fact]
    public void EveryFieldCombination_RestoresBaselineExactlyWhereCurrentEqualsAJournaledValue_AndNothingElse()
    {
        // Exhaustive per-field truth table over baseline x applied x pending x current for the three
        // independent string fields, expressed as a property rather than a copy of the implementation.
        foreach (StringValue baseline in StringDomain)
        {
            foreach (StringValue applied in StringDomain)
            {
                foreach (StringValue? pending in StringDomain.Select(value => (StringValue?)value).Append(null))
                {
                    foreach (StringValue current in StringDomain)
                    {
                        Snapshot merged = Restorer.MergeOwnedRestore(
                            Tuple(NoDword, baseline, baseline, baseline),
                            JournalOf(Tuple(NoDword, baseline, baseline, baseline), Tuple(NoDword, applied, applied, applied), pending is null ? null : Tuple(NoDword, pending.Value, pending.Value, pending.Value)));
                        _ = merged;
                        Snapshot result = Restorer.MergeOwnedRestore(
                            Tuple(NoDword, current, current, current),
                            JournalOf(
                                Tuple(NoDword, baseline, baseline, baseline),
                                Tuple(NoDword, applied, applied, applied),
                                pending is null ? null : Tuple(NoDword, pending.Value, pending.Value, pending.Value)));

                        bool owned = current == applied || (pending is not null && current == pending.Value);
                        StringValue expected = owned ? baseline : current;
                        Assert.Equal(expected, result.ProxyServer);
                        Assert.Equal(expected, result.ProxyOverride);
                        Assert.Equal(expected, result.AutoConfigUrl);
                    }
                }
            }
        }
    }

    [Fact]
    public void EnableSwitch_IsRestoredOnlyWhenTheSameJournalProvesBothTheServerAndTheSwitch()
    {
        foreach (Dword baselineEnable in DwordDomain)
        {
            foreach (Dword appliedEnable in DwordDomain)
            {
                foreach (Dword? pendingEnable in DwordDomain.Select(value => (Dword?)value).Append(null))
                {
                    foreach (Dword currentEnable in DwordDomain)
                    {
                        foreach (StringValue server in new[] { Absent, Text("A"), Text("B"), Text("C") })
                        {
                            StringValue baselineServer = Text("B");
                            StringValue appliedServer = Text("A");
                            StringValue? pendingServer = pendingEnable is null ? null : Text("C");
                            Snapshot result = Restorer.MergeOwnedRestore(
                                Tuple(currentEnable, server, Absent, Absent),
                                JournalOf(
                                    Tuple(baselineEnable, baselineServer, Absent, Absent),
                                    Tuple(appliedEnable, appliedServer, Absent, Absent),
                                    pendingEnable is null
                                        ? null
                                        : Tuple(pendingEnable.Value, pendingServer!.Value, Absent, Absent)));

                            bool ownsServer = server == appliedServer || pendingServer is not null && server == pendingServer.Value;
                            bool ownsSwitch = ownsServer
                                && (currentEnable == appliedEnable || pendingEnable is not null && currentEnable == pendingEnable.Value);
                            Assert.Equal(ownsSwitch ? baselineEnable : currentEnable, result.ProxyEnable);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void ForeignServerReplacement_NeverLetsAMatchingEnableFlagBeRolledBack()
    {
        // Another application installed its own server and also left ProxyEnable at the value we wrote.
        Snapshot current = Tuple(Dw(1), Text("corp:3128"), Absent, Absent);
        Journal journal = JournalOf(
            Tuple(Dw(0), Absent, Absent, Absent),
            Tuple(Dw(1), Text("127.0.0.1:7890"), Absent, Absent),
            null);

        Snapshot result = Restorer.MergeOwnedRestore(current, journal);

        Assert.Equal(current, result);
    }

    [Fact]
    public void FullyOwnedState_RestoresTheCompleteBaselineIncludingAbsence()
    {
        foreach (StringValue baseline in StringDomain)
        {
            Snapshot baselineTuple = Tuple(Dw(0), baseline, baseline, baseline);
            Snapshot applied = Tuple(Dw(1), Text("127.0.0.1:7890"), Text("<local>"), Text("http://127.0.0.1/pac"));

            Snapshot result = Restorer.MergeOwnedRestore(applied, JournalOf(baselineTuple, applied, null));

            Assert.Equal(baselineTuple, result);
        }
    }

    [Fact]
    public void PartialApply_EitherJournaledTupleAuthorizesTheRestoreOfThatField()
    {
        Snapshot baseline = Tuple(Dw(0), Text("old"), Absent, Absent);
        Snapshot applied = Tuple(Dw(0), Text("old"), Absent, Absent);
        Snapshot pending = Tuple(Dw(1), Text("new"), Text("<local>"), Absent);
        Journal journal = JournalOf(baseline, applied, pending);

        // Crash after the server write only: server is pending, enable still the pre-write value.
        Assert.Equal(baseline, Restorer.MergeOwnedRestore(Tuple(Dw(0), Text("new"), Absent, Absent), journal));
        // Crash after every write: everything is the pending tuple.
        Assert.Equal(baseline, Restorer.MergeOwnedRestore(pending, journal));
        // Crash before any write: nothing changed yet.
        Assert.Equal(baseline, Restorer.MergeOwnedRestore(applied, journal));
        // The user changed the override after the crash: that field is theirs.
        Snapshot userTouched = Tuple(Dw(1), Text("new"), Text("custom"), Absent);
        Assert.Equal(Tuple(Dw(0), Text("old"), Text("custom"), Absent), Restorer.MergeOwnedRestore(userTouched, journal));
    }

    [Fact]
    public void Merge_IsIdempotent_ForGeneratedJournalsAndRegistryStates()
    {
        Random random = new(20261109);
        for (int round = 0; round < 5000; round++)
        {
            Journal journal = JournalOf(
                RandomTuple(random),
                RandomTuple(random),
                random.Next(2) == 0 ? null : RandomTuple(random));
            Snapshot current = random.Next(3) == 0 ? journal.Applied : RandomTuple(random);

            Snapshot once = Restorer.MergeOwnedRestore(current, journal);
            Snapshot twice = Restorer.MergeOwnedRestore(once, journal);

            Assert.True(once == twice, $"Restore is not idempotent for round {round}.");
        }
    }

    [Fact]
    public void Merge_NeverInventsAValue_ForGeneratedInputs()
    {
        Random random = new(20261109);
        for (int round = 0; round < 5000; round++)
        {
            Journal journal = JournalOf(RandomTuple(random), RandomTuple(random), random.Next(2) == 0 ? null : RandomTuple(random));
            Snapshot current = RandomTuple(random);

            Snapshot result = Restorer.MergeOwnedRestore(current, journal);

            Assert.True(result.ProxyEnable == current.ProxyEnable || result.ProxyEnable == journal.Baseline.ProxyEnable);
            Assert.True(result.ProxyServer == current.ProxyServer || result.ProxyServer == journal.Baseline.ProxyServer);
            Assert.True(result.ProxyOverride == current.ProxyOverride || result.ProxyOverride == journal.Baseline.ProxyOverride);
            Assert.True(result.AutoConfigUrl == current.AutoConfigUrl || result.AutoConfigUrl == journal.Baseline.AutoConfigUrl);
        }
    }

    [Fact]
    public void Merge_RejectsNullArguments()
    {
        Snapshot any = Tuple(NoDword, Absent, Absent, Absent);

        Assert.Throws<ArgumentNullException>(() => Restorer.MergeOwnedRestore(null!, JournalOf(any, any, null)));
        Assert.Throws<ArgumentNullException>(() => Restorer.MergeOwnedRestore(any, null!));
        Assert.Throws<ArgumentNullException>(() => Restorer.Restore(null!, new FakeJournalStore(null)));
        Assert.Throws<ArgumentNullException>(() => Restorer.Restore(new FakeRegistryStore(any), null!));
    }

    [Fact]
    public void Restore_WithoutAJournalNeitherReadsNorWritesTheRegistry()
    {
        FakeRegistryStore registry = new(Tuple(Dw(1), Text("foreign"), Absent, Absent))
        {
            ReadFailure = new InvalidOperationException("The registry must not even be read without ownership."),
        };
        FakeJournalStore journal = new(null);

        bool restored = Restorer.Restore(registry, journal);

        Assert.False(restored);
        Assert.Equal(0, registry.Reads);
        Assert.Equal(0, registry.Writes);
        Assert.Equal(0, journal.Clears);
    }

    [Fact]
    public void Restore_WritesTheRegistryBeforeClearingTheJournal_AndReportsTrue()
    {
        List<string> log = [];
        Snapshot baseline = Tuple(Dw(0), Absent, Absent, Absent);
        Snapshot applied = Tuple(Dw(1), Text("127.0.0.1:7890"), Absent, Absent);
        FakeRegistryStore registry = new(applied) { Log = log };
        FakeJournalStore journal = new(JournalOf(baseline, applied, null)) { Log = log };

        bool restored = Restorer.Restore(registry, journal);

        Assert.True(restored);
        Assert.Equal(baseline, registry.Current);
        Assert.Null(journal.Current);
        Assert.Equal(["journal.read", "registry.read", "registry.write", "journal.clear"], log);
    }

    /// <summary>Gets one row per effect boundary of the restore.</summary>
    public static TheoryData<string> FailingSteps => ["journal.read", "registry.read", "registry.write", "journal.clear"];

    [Theory]
    [MemberData(nameof(FailingSteps))]
    public void Restore_AfterAFailureAtAnyStep_KeepsOwnershipEvidenceAndAnExactRetrySucceedsOnce(string failingStep)
    {
        Snapshot baseline = Tuple(Dw(0), Text("old"), Text("old-override"), Absent);
        Snapshot applied = Tuple(Dw(1), Text("127.0.0.1:7890"), Text("<local>"), Text("http://pac"));
        FakeRegistryStore registry = new(applied);
        Journal original = JournalOf(baseline, applied, null);
        FakeJournalStore journal = new(original);
        IOException failure = new("scripted " + failingStep);
        switch (failingStep)
        {
            case "journal.read":
                journal.ReadFailure = failure;
                break;
            case "registry.read":
                registry.ReadFailure = failure;
                break;
            case "registry.write":
                registry.WriteFailure = failure;
                break;
            default:
                journal.ClearFailure = failure;
                break;
        }

        IOException thrown = Assert.Throws<IOException>(() => Restorer.Restore(registry, journal));
        Assert.Same(failure, thrown);

        // Only a failed clear may have changed the registry (it was already restored); in every case
        // the journal still proves ownership so the exact retry is possible.
        Assert.NotNull(journal.CurrentOrUnreadable);
        journal.ReadFailure = null;
        registry.ReadFailure = null;
        registry.WriteFailure = null;
        journal.ClearFailure = null;
        int writesBeforeRetry = registry.Writes;

        bool retried = Restorer.Restore(registry, journal);

        Assert.True(retried);
        Assert.Equal(baseline, registry.Current);
        Assert.Null(journal.CurrentOrUnreadable);
        Assert.Equal(writesBeforeRetry + 1, registry.Writes);
        Assert.False(Restorer.Restore(registry, journal), "A cleared journal must make the next run a no-op.");
        Assert.Equal(writesBeforeRetry + 1, registry.Writes);
    }

    [Fact]
    public void Restore_WhenTheUserChangedFieldsMeanwhile_KeepsExactlyTheirValues_AcrossRetries()
    {
        Snapshot baseline = Tuple(Dw(0), Text("old"), Absent, Text("http://old-pac"));
        Snapshot applied = Tuple(Dw(1), Text("127.0.0.1:7890"), Text("<local>"), Absent);
        Snapshot afterUser = Tuple(Dw(1), Text("127.0.0.1:7890"), Text("user-override"), Absent);
        FakeRegistryStore registry = new(afterUser);
        FakeJournalStore journal = new(JournalOf(baseline, applied, null)) { ClearFailure = new IOException("first clear fails") };

        Assert.Throws<IOException>(() => Restorer.Restore(registry, journal));
        journal.ClearFailure = null;
        Assert.True(Restorer.Restore(registry, journal));

        // The PAC value Clash# removed is restored; the override the user changed is theirs.
        Assert.Equal(Tuple(Dw(0), Text("old"), Text("user-override"), Text("http://old-pac")), registry.Current);
    }

    [Fact]
    public void RunnerRestoreDelegate_ForwardsToTheSameOwnershipRestorer()
    {
        Snapshot baseline = Tuple(Dw(0), Absent, Absent, Absent);
        Snapshot applied = Tuple(Dw(1), Text("127.0.0.1:7890"), Absent, Absent);
        FakeRegistryStore registry = new(applied);
        FakeJournalStore journal = new(JournalOf(baseline, applied, null));

        bool restored = Watchdog.RestoreOwnedProxy(registry, journal);

        Assert.True(restored);
        Assert.Equal(baseline, registry.Current);
        Assert.False(Watchdog.RestoreOwnedProxy(registry, journal));
    }

    /// <summary>Gets every defined journal phase so a new phase must be considered.</summary>
    [Fact]
    public void Journal_PhaseAndPendingTupleMustAgree_ForEveryPhaseValue()
    {
        Snapshot tuple = Tuple(Dw(1), Text("A"), Absent, Absent);
        foreach (Phase phase in Enum.GetValues<Phase>())
        {
            Journal withPending = new(Journal.CurrentSchemaVersion, tuple, tuple, phase, tuple);
            Journal withoutPending = new(Journal.CurrentSchemaVersion, tuple, tuple, phase, null);
            bool applying = phase == Phase.Applying;

            Assert.Equal(applying, Record.Exception(withPending.Validate) is null);
            Assert.Equal(!applying, Record.Exception(withoutPending.Validate) is null);
        }

        Assert.Throws<InvalidDataException>(new Journal(Journal.CurrentSchemaVersion, tuple, tuple, (Phase)99, null).Validate);
        Assert.Throws<InvalidDataException>(new Journal(0, tuple, tuple, Phase.Applied, null).Validate);
        Assert.Throws<InvalidDataException>(new Journal(Journal.CurrentSchemaVersion + 1, tuple, tuple, Phase.Applied, null).Validate);
    }

    [Fact]
    public void Journal_RejectsInconsistentTuples()
    {
        Snapshot good = Tuple(Dw(1), Text("A"), Absent, Absent);
        Snapshot[] invalid =
        [
            // Missing DWORD with a nonzero value.
            new Snapshot(new Dword(false, 7), Absent, Absent, Absent),
            // Exists with null text.
            new Snapshot(Dw(1), new StringValue(true, null, StringKind.String), Absent, Absent),
            // Absent with text.
            new Snapshot(Dw(1), new StringValue(false, "x", StringKind.None), Absent, Absent),
            // Exists with the None kind.
            new Snapshot(Dw(1), new StringValue(true, "x", StringKind.None), Absent, Absent),
            // Absent with a storage kind.
            new Snapshot(Dw(1), new StringValue(false, null, StringKind.String), Absent, Absent),
            // Undefined storage kind.
            new Snapshot(Dw(1), new StringValue(true, "x", (StringKind)9), Absent, Absent),
        ];

        foreach (Snapshot bad in invalid)
        {
            Assert.Throws<InvalidDataException>(new Journal(Journal.CurrentSchemaVersion, bad, good, Phase.Applied, null).Validate);
            Assert.Throws<InvalidDataException>(new Journal(Journal.CurrentSchemaVersion, good, bad, Phase.Applied, null).Validate);
            Assert.Throws<InvalidDataException>(new Journal(Journal.CurrentSchemaVersion, good, good, Phase.Applying, bad).Validate);
        }

        new Journal(Journal.CurrentSchemaVersion, good, good, Phase.Applied, null).Validate();
    }

    private static Snapshot Tuple(Dword enable, StringValue server, StringValue overrides, StringValue pac) =>
        new(enable, server, overrides, pac);

    private static Journal JournalOf(Snapshot baseline, Snapshot applied, Snapshot? pending) =>
        new(
            Journal.CurrentSchemaVersion,
            baseline,
            applied,
            pending is null ? Phase.Applied : Phase.Applying,
            pending);

    private static Snapshot RandomTuple(Random random) => Tuple(
        DwordDomain[random.Next(DwordDomain.Length)],
        StringDomain[random.Next(StringDomain.Length)],
        StringDomain[random.Next(StringDomain.Length)],
        StringDomain[random.Next(StringDomain.Length)]);

    private sealed class FakeRegistryStore(Snapshot initial) : IRegistryStore
    {
        internal Snapshot Current { get; private set; } = initial;

        internal int Reads { get; private set; }

        internal int Writes { get; private set; }

        internal Exception? ReadFailure { get; set; }

        internal Exception? WriteFailure { get; set; }

        internal List<string>? Log { get; set; }

        public Snapshot Read()
        {
            Reads++;
            Log?.Add("registry.read");
            return ReadFailure is null ? Current : throw ReadFailure;
        }

        public void Write(Snapshot snapshot)
        {
            Log?.Add("registry.write");
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            Writes++;
            Current = snapshot;
        }
    }

    private sealed class FakeJournalStore(Journal? initial) : IJournalStore
    {
        private Journal? _current = initial;

        internal Journal? Current => _current;

        /// <summary>Gets the stored journal, even while reads are scripted to fail.</summary>
        internal Journal? CurrentOrUnreadable => _current;

        internal int Clears { get; private set; }

        internal Exception? ReadFailure { get; set; }

        internal Exception? ClearFailure { get; set; }

        internal List<string>? Log { get; set; }

        public Journal? Read()
        {
            Log?.Add("journal.read");
            return ReadFailure is null ? _current : throw ReadFailure;
        }

        public void Write(Journal journal) => _current = journal;

        public void Clear()
        {
            Log?.Add("journal.clear");
            if (ClearFailure is not null)
            {
                throw ClearFailure;
            }

            Clears++;
            _current = null;
        }
    }
}

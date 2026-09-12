using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerCleanupTransactionStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadRecoversOnlyTheExactVerifiedUninstall(bool activePresent)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, activePresent ? verified : null);
        var ledger = new MemoryLedger(events, OwnedLedger().BeginTerminal(verified));
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        Assert.Equal(verified, await store.LoadAsync(CancellationToken.None));
        Assert.Equal(0, inner.SaveCalls);
        Assert.Equal(0, ledger.SaveCalls);
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("target")]
    [InlineData("version")]
    [InlineData("payload")]
    public async Task LoadRejectsConflictingActiveAndTerminalIdentity(string field)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, ChangeIdentity(verified, field));
        var ledger = new MemoryLedger(events, OwnedLedger().BeginTerminal(verified));
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() => store.LoadAsync(CancellationToken.None));

        Assert.Equal("installer.directory_ledger.terminal_identity_mismatch", failure.DiagnosticCode);
        Assert.Equal(0, inner.ClearCalls);
        Assert.Equal(0, ledger.SaveCalls);
    }

    [Fact]
    public async Task ClearPersistsAndReobservesTerminalBeforeDeletingJournalAndOnlySuppressesThisSession()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        WindowsInstallerDirectoryLedger owned = OwnedLedger();
        var inner = new MemoryStore(events, verified);
        var ledger = new MemoryLedger(events, owned);
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        await store.ClearVerifiedAsync(verified.Journal.TransactionId, verified.ContentHash, CancellationToken.None);

        Assert.Equal(new[] { "inner.load", "ledger.load", "ledger.save", "ledger.load", "inner.clear", "inner.load", "ledger.load" }, events);
        Assert.Null(inner.Current);
        Assert.Equal(verified, ledger.Current!.Terminal);
        Assert.Equal(owned.Directories, ledger.Current.Directories);
        Assert.Equal(owned.Generation + 1, ledger.Current.Generation);
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        var reopened = new WindowsInstallerCleanupTransactionStore(inner, ledger);
        Assert.Equal(verified, await reopened.LoadAsync(CancellationToken.None));
        Assert.Equal(0, inner.SaveCalls);
        Assert.Equal(0, ledger.DeleteCalls);
    }

    [Fact]
    public async Task MissingLedgerBecomesTerminalWithoutInventingDirectoryOwnership()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, verified);
        var ledger = new MemoryLedger(events, null);
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        await store.ClearVerifiedAsync(verified.Journal.TransactionId, verified.ContentHash, CancellationToken.None);

        Assert.NotNull(ledger.Current);
        Assert.Empty(ledger.Current.Directories);
        Assert.Equal(verified, ledger.Current.Terminal);
        Assert.Equal(1, ledger.Current.Generation);
    }

    [Fact]
    public async Task ReopenedTerminalCanReverifyAndClearWithoutRecreatingAnOrdinaryJournal()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, null);
        var ledger = new MemoryLedger(events, OwnedLedger().BeginTerminal(verified));
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        Assert.Equal(verified, await store.SaveAsync(verified.Journal, verified.ContentHash, CancellationToken.None));
        await store.ClearVerifiedAsync(verified.Journal.TransactionId, verified.ContentHash, CancellationToken.None);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.Equal(verified, await new WindowsInstallerCleanupTransactionStore(inner, ledger).LoadAsync(CancellationToken.None));
        Assert.Equal(0, inner.SaveCalls);
        Assert.Equal(0, inner.ClearCalls);
        Assert.Equal(0, ledger.DeleteCalls);
    }

    [Theory]
    [InlineData("missing-hash")]
    [InlineData("wrong-hash")]
    [InlineData("transaction")]
    [InlineData("target")]
    [InlineData("version")]
    [InlineData("payload")]
    [InlineData("phase")]
    [InlineData("operation")]
    public async Task TerminalOnlySaveCannotAdoptAnotherTransactionOrRegress(string mutation)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        InstallerTransactionJournal requested = mutation switch
        {
            "missing-hash" or "wrong-hash" => verified.Journal,
            "phase" => verified.Journal with { Phase = InstallerTransactionPhase.PackageCommitted, Generation = 4 },
            "operation" => verified.Journal with { Operation = InstallerOperation.Install },
            _ => ChangeIdentity(verified, mutation).Journal,
        };
        string? expectedHash = mutation switch { "missing-hash" => null, "wrong-hash" => new string('d', 64), _ => verified.ContentHash };
        var inner = new MemoryStore(events, null);
        var ledger = new MemoryLedger(events, OwnedLedger().BeginTerminal(verified));
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.SaveAsync(requested, expectedHash, CancellationToken.None));

        Assert.Equal("installer.transaction.write_conflict", failure.DiagnosticCode);
        Assert.Equal(0, inner.SaveCalls);
        Assert.Equal(0, ledger.SaveCalls);
        Assert.Equal(verified, ledger.Current!.Terminal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearRejectsWrongIdentityWithoutChangingEitherStore(bool wrongHash)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, null);
        var ledger = new MemoryLedger(events, OwnedLedger().BeginTerminal(verified));
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() => store.ClearVerifiedAsync(
            wrongHash ? verified.Journal.TransactionId : new string('d', 64),
            wrongHash ? new string('d', 64) : verified.ContentHash, CancellationToken.None));

        Assert.Equal("installer.transaction.clear_conflict", failure.DiagnosticCode);
        Assert.Equal(0, inner.ClearCalls);
        Assert.Equal(0, ledger.SaveCalls);
    }

    [Theory]
    [InlineData("before-terminal", false)]
    [InlineData("after-terminal", false)]
    [InlineData("before-clear", false)]
    [InlineData("after-clear", true)]
    [InlineData("after-clear-observation", true)]
    public async Task IoFailurePreservesTheActualDurableCutPointForReopen(string cut, bool journalDeleted)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, verified);
        var ledger = new MemoryLedger(events, OwnedLedger());
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);
        var injected = new IOException("Injected cleanup checkpoint failure.");
        Action fail = () => throw injected;
        Inject(cut, inner, ledger, fail);

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            store.ClearVerifiedAsync(verified.Journal.TransactionId, verified.ContentHash, CancellationToken.None));

        Assert.Same(injected, actual);
        Assert.Equal(journalDeleted ? null : verified, inner.Current);
        Assert.Equal(cut == "before-terminal" ? null : verified, ledger.Current!.Terminal);
        inner.ResetFaults();
        ledger.ResetFaults();
        Assert.Equal(verified, await store.LoadAsync(CancellationToken.None));
        Assert.Equal(verified, await new WindowsInstallerCleanupTransactionStore(inner, ledger).LoadAsync(CancellationToken.None));
        Assert.Equal(0, ledger.DeleteCalls);
    }

    [Theory]
    [InlineData("before-terminal", false)]
    [InlineData("after-terminal", false)]
    [InlineData("before-clear", false)]
    [InlineData("after-clear", true)]
    [InlineData("after-clear-observation", true)]
    public async Task CancellationNeverDiscardsTerminalRecovery(string cut, bool journalDeleted)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, verified);
        var ledger = new MemoryLedger(events, OwnedLedger());
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);
        using var cancellation = new CancellationTokenSource();
        Inject(cut, inner, ledger, () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ClearVerifiedAsync(verified.Journal.TransactionId, verified.ContentHash, cancellation.Token));

        Assert.Equal(journalDeleted ? null : verified, inner.Current);
        Assert.Equal(cut == "before-terminal" ? null : verified, ledger.Current!.Terminal);
        inner.ResetFaults();
        ledger.ResetFaults();
        Assert.Equal(verified, await new WindowsInstallerCleanupTransactionStore(inner, ledger).LoadAsync(CancellationToken.None));
        Assert.Equal(0, ledger.DeleteCalls);
    }

    [Fact]
    public async Task UnobservedTerminalWriteCannotDeleteTheActiveJournal()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, verified);
        var ledger = new MemoryLedger(events, OwnedLedger());
        ledger.AfterSave = () => ledger.Current = null;
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.ClearVerifiedAsync(verified.Journal.TransactionId, verified.ContentHash, CancellationToken.None));

        Assert.Equal("installer.directory_ledger.terminal_not_observed", failure.DiagnosticCode);
        Assert.Equal(verified, inner.Current);
        Assert.Equal(0, inner.ClearCalls);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public async Task OtherOperationsUseTheInnerClearWithoutCreatingTerminalState(InstallerOperation operation)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified(operation);
        var inner = new MemoryStore(events, verified);
        var ledger = new MemoryLedger(events, OwnedLedger());
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        await store.ClearVerifiedAsync(verified.Journal.TransactionId, verified.ContentHash, CancellationToken.None);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.Null(ledger.Current!.Terminal);
        Assert.Equal(0, ledger.SaveCalls);
        Assert.Equal(1, inner.ClearCalls);
    }

    [Fact]
    public async Task OrdinarySavePreservesTheOriginalJournalCas()
    {
        List<string> events = [];
        InstallerTransactionSnapshot prepared = InstallerTransactionSnapshot.Create(Verified(InstallerOperation.Install).Journal with
        {
            Phase = InstallerTransactionPhase.Prepared,
            Generation = 1,
        });
        var inner = new MemoryStore(events, null);
        var ledger = new MemoryLedger(events, OwnedLedger());
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);

        Assert.Equal(prepared, await store.SaveAsync(prepared.Journal, null, CancellationToken.None));
        Assert.Equal(prepared, await store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.SaveAsync(prepared.Journal, null, CancellationToken.None));
        Assert.Equal(0, ledger.SaveCalls);
    }

    [Fact]
    public async Task ClearedSessionDoesNotSuppressAReplacementTerminal()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = Verified();
        var inner = new MemoryStore(events, verified);
        var ledger = new MemoryLedger(events, OwnedLedger());
        var store = new WindowsInstallerCleanupTransactionStore(inner, ledger);
        await store.ClearVerifiedAsync(verified.Journal.TransactionId, verified.ContentHash, CancellationToken.None);
        ledger.Current = OwnedLedger().BeginTerminal(ChangeIdentity(verified, "transaction"));

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() => store.LoadAsync(CancellationToken.None));

        Assert.Equal("installer.directory_ledger.terminal_identity_mismatch", failure.DiagnosticCode);
    }

    private static void Inject(string cut, MemoryStore inner, MemoryLedger ledger, Action failure)
    {
        switch (cut)
        {
            case "before-terminal": ledger.BeforeSave = failure; break;
            case "after-terminal": ledger.AfterSave = failure; break;
            case "before-clear": inner.BeforeClear = failure; break;
            case "after-clear": inner.AfterClear = failure; break;
            case "after-clear-observation": inner.AfterClear = () => ledger.BeforeLoad = failure; break;
            default: throw new ArgumentOutOfRangeException(nameof(cut));
        }
    }

    private static InstallerTransactionSnapshot Verified(InstallerOperation operation = InstallerOperation.Uninstall) =>
        InstallerTransactionSnapshot.Create(new InstallerTransactionJournal(InstallerTransactionJournal.CurrentSchema,
            new string('a', 64), operation, "S-1-5-21-100-200-300-1001", false, "1.0.0.0", new string('b', 64),
            InstallerTransactionPhase.Verified, 5));

    private static InstallerTransactionSnapshot ChangeIdentity(InstallerTransactionSnapshot state, string field) =>
        InstallerTransactionSnapshot.Create(field switch
        {
            "transaction" => state.Journal with { TransactionId = new string('c', 64) },
            "target" => state.Journal with { TargetSid = "S-1-5-21-100-200-300-1002" },
            "version" => state.Journal with { ExpectedPackageVersion = "1.0.1.0" },
            "payload" => state.Journal with { InstallerPayloadSha256 = new string('c', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        });

    private static WindowsInstallerDirectoryLedger OwnedLedger() => WindowsInstallerDirectoryLedger.Empty.RecordCreated(
        InstallerDirectoryRole.InstallerVersion, new WindowsFileIdentity(11, 22));

    private sealed class MemoryStore(List<string> events, InstallerTransactionSnapshot? initial) : IInstallerTransactionStore
    {
        internal InstallerTransactionSnapshot? Current { get; set; } = initial;
        internal int SaveCalls { get; private set; }
        internal int ClearCalls { get; private set; }
        internal Action? BeforeClear { get; set; }
        internal Action? AfterClear { get; set; }

        public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("inner.load");
            return Task.FromResult(Current);
        }

        public Task<InstallerTransactionSnapshot> SaveAsync(InstallerTransactionJournal journal, string? expectedCurrentHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current?.ContentHash != expectedCurrentHash)
            {
                throw new InstallerProtocolException("installer.transaction.write_conflict");
            }
            events.Add("inner.save");
            SaveCalls++;
            Current = InstallerTransactionSnapshot.Create(journal);
            return Task.FromResult(Current);
        }

        public Task ClearVerifiedAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(Current);
            Assert.Equal(Current.Journal.TransactionId, transactionId);
            Assert.Equal(Current.ContentHash, expectedCurrentHash);
            events.Add("inner.clear");
            ClearCalls++;
            BeforeClear?.Invoke();
            Current = null;
            AfterClear?.Invoke();
            return Task.CompletedTask;
        }

        internal void ResetFaults() { BeforeClear = null; AfterClear = null; }
    }

    private sealed class MemoryLedger(List<string> events, WindowsInstallerDirectoryLedger? initial) : IWindowsInstallerDirectoryLedgerPersistence
    {
        internal WindowsInstallerDirectoryLedger? Current { get; set; } = initial;
        internal int SaveCalls { get; private set; }
        internal int DeleteCalls { get; private set; }
        internal Action? BeforeLoad { get; set; }
        internal Action? BeforeSave { get; set; }
        internal Action? AfterSave { get; set; }

        public Task<WindowsInstallerDirectoryLedger?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("ledger.load");
            BeforeLoad?.Invoke();
            return Task.FromResult(Current);
        }

        public Task SaveAsync(WindowsInstallerDirectoryLedger? expected, WindowsInstallerDirectoryLedger desired,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(expected, Current);
            events.Add("ledger.save");
            SaveCalls++;
            BeforeSave?.Invoke();
            Current = desired;
            AfterSave?.Invoke();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(WindowsInstallerDirectoryLedger expected, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            throw new InvalidOperationException("The transaction store must not delete the terminal ledger.");
        }

        internal void ResetFaults() { BeforeLoad = null; BeforeSave = null; AfterSave = null; }
    }
}

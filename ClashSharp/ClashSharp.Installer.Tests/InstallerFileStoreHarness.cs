using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>A per-test directory that removes read-only files and reparse points on cleanup.</summary>
internal sealed class InstallerScratchDirectory : IDisposable
{
    internal InstallerScratchDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ClashSharp.Installer.Scratch.{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string Combine(params string[] segments) => System.IO.Path.Combine([Path, .. segments]);

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of a test sandbox only.
            }
        }

        Directory.Delete(Path, recursive: true);
    }
}

/// <summary>A root guard whose behaviour each test scripts; it never inspects real ACLs.</summary>
internal sealed class ScriptedRootGuard : IInstallerTransactionRootGuard
{
    internal List<string> Roots { get; } = [];

    internal Func<string, CancellationToken, Task>? Behavior { get; set; }

    public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
    {
        Roots.Add(absoluteRootPath);
        return Behavior?.Invoke(absoluteRootPath, cancellationToken) ?? Task.CompletedTask;
    }
}

/// <summary>Uniform view over the two guarded single-document stores so one contract covers both.</summary>
internal abstract class FileStoreHarness : IDisposable
{
    internal string Root { get; init; } = string.Empty;

    internal ScriptedRootGuard Guard { get; init; } = new();

    internal abstract string FileName { get; }

    /// <summary>Canonical documents in write order; the last one is the only clearable terminal.</summary>
    internal abstract byte[][] Chain { get; }

    internal string DocumentPath => System.IO.Path.Combine(Root, FileName);

    internal int TerminalIndex => Chain.Length - 1;

    internal abstract bool IsLegalStep(int from, int to);

    internal abstract Task<string?> LoadHashAsync(CancellationToken cancellationToken);

    internal abstract Task<string> SaveAsync(int chainIndex, string? expectedHash, CancellationToken cancellationToken);

    internal abstract Task ClearAsync(string hash, CancellationToken cancellationToken);

    internal abstract FileStoreHarness Clone();

    public abstract void Dispose();

    internal static FileStoreHarness Create(string kind, string root, ScriptedRootGuard? guard = null) => kind switch
    {
        "transaction" => new TransactionHarness(root, guard ?? new ScriptedRootGuard()),
        "ledger" => new LedgerHarness(root, guard ?? new ScriptedRootGuard()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal string[] RootFiles() => Directory.Exists(Root)
        ? Directory.GetFileSystemEntries(Root)
            .Select(static entry => System.IO.Path.GetFileName(entry))
            .Order(StringComparer.Ordinal)
            .ToArray()
        : [];

    private sealed class TransactionHarness : FileStoreHarness
    {
        private readonly FileInstallerTransactionStore _store;
        private readonly InstallerTransactionJournal[] _journals;

        internal TransactionHarness(string root, ScriptedRootGuard guard)
        {
            Root = root;
            Guard = guard;
            _store = new FileInstallerTransactionStore(root, guard);
            InstallerTransactionJournal journal = InstallerTestData.Journal();
            _journals =
            [
                journal,
                journal = journal.TransitionTo(InstallerTransactionPhase.MachineReserved),
                journal = journal.TransitionTo(InstallerTransactionPhase.PackageCommitted),
                journal = journal.TransitionTo(InstallerTransactionPhase.MachineCommitted),
                journal.TransitionTo(InstallerTransactionPhase.Verified),
            ];
            Chain = _journals.Select(InstallerTransactionCodec.Serialize).ToArray();
        }

        internal override string FileName => FileInstallerTransactionStore.JournalFileName;

        internal override byte[][] Chain { get; }

        internal override bool IsLegalStep(int from, int to) => to == from || to == from + 1;

        internal override async Task<string?> LoadHashAsync(CancellationToken cancellationToken) =>
            (await _store.LoadAsync(cancellationToken))?.ContentHash;

        internal override async Task<string> SaveAsync(int chainIndex, string? expectedHash, CancellationToken cancellationToken) =>
            (await _store.SaveAsync(_journals[chainIndex], expectedHash, cancellationToken)).ContentHash;

        internal override Task ClearAsync(string hash, CancellationToken cancellationToken) =>
            _store.ClearVerifiedAsync(_journals[^1].TransactionId, hash, cancellationToken);

        internal override FileStoreHarness Clone() => new TransactionHarness(Root, Guard);

        public override void Dispose() => _store.Dispose();
    }

    private sealed class LedgerHarness : FileStoreHarness
    {
        private readonly FileInstallerCertificateOwnershipStore _store;
        private readonly InstallerCertificateOwnershipLedger[] _ledgers;

        internal LedgerHarness(string root, ScriptedRootGuard guard)
        {
            Root = root;
            Guard = guard;
            _store = new FileInstallerCertificateOwnershipStore(root, guard);
            InstallerCertificateOwnershipLedger first = InstallerTestData.CertificateLedger();
            _ledgers = [first, first.PrepareRemoval()];
            Chain = _ledgers.Select(InstallerCertificateOwnershipCodec.Serialize).ToArray();
        }

        internal override string FileName => FileInstallerCertificateOwnershipStore.LedgerFileName;

        internal override byte[][] Chain { get; }

        internal override bool IsLegalStep(int from, int to) => to == from || to == from + 1;

        internal override async Task<string?> LoadHashAsync(CancellationToken cancellationToken) =>
            (await _store.LoadAsync(cancellationToken))?.ContentHash;

        internal override async Task<string> SaveAsync(int chainIndex, string? expectedHash, CancellationToken cancellationToken) =>
            (await _store.SaveAsync(_ledgers[chainIndex], expectedHash, cancellationToken)).ContentHash;

        internal override Task ClearAsync(string hash, CancellationToken cancellationToken) =>
            _store.ClearUnreferencedAsync(_ledgers[^1].LedgerId, hash, cancellationToken);

        internal override FileStoreHarness Clone() => new LedgerHarness(Root, Guard);

        public override void Dispose() => _store.Dispose();
    }
}

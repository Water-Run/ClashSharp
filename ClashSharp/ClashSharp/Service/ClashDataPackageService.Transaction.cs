using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Service;

/// <summary>Observable durable cuts used only to simulate a process crash in transaction tests.</summary>
internal enum DataPackageTransactionCheckpoint
{
    ManifestPersisted = 0,
    ImportSettingsApplied = 1,
    ImportFileApplied = 2,
    TransactionApplied = 3,
    ResetMutationCompleted = 4,
    ResetSettingsApplied = 5,
    RollbackPersisted = 6,
    RollbackFileApplied = 7,
    TransactionCleanupStarting = 8,
    TransactionPayloadCleanupCompleted = 9,
}

/// <summary>Marks an injected failure as an abrupt process stop that must bypass in-process rollback.</summary>
internal sealed class DataPackageSimulatedCrashException(string message) : Exception(message);

/// <summary>
/// A retained settings/files generation whose external activation can still be committed or rolled back.
/// </summary>
internal sealed class DataPackageTransactionReceipt : IAsyncDisposable
{
    private readonly Func<bool, CancellationToken, Task> _complete;
    private readonly SemaphoreSlim _decisionGate = new(1, 1);
    private IDisposable? _transactionLease;
    private int _decision;
    private bool _completed;
    private bool _disposed;

    internal DataPackageTransactionReceipt(
        Guid operationId,
        IDisposable transactionLease,
        Func<bool, CancellationToken, Task> complete)
    {
        OperationId = operationId;
        _transactionLease = transactionLease ?? throw new ArgumentNullException(nameof(transactionLease));
        _complete = complete ?? throw new ArgumentNullException(nameof(complete));
    }

    public Guid OperationId { get; }

    public Task CommitAsync(CancellationToken cancellationToken) =>
        CompleteAsync(decision: 1, rollback: false, cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken) =>
        CompleteAsync(decision: 2, rollback: true, cancellationToken);

    private async Task CompleteAsync(int decision, bool rollback, CancellationToken cancellationToken)
    {
        await _decisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_decision != 0 && _decision != decision)
            {
                throw new InvalidOperationException(
                    "A data package transaction cannot change its completion decision.");
            }

            if (_completed)
            {
                return;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            _decision = decision;
            await _complete(rollback, cancellationToken).ConfigureAwait(false);
            _completed = true;
            ReleaseTransactionLease();
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _decisionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseTransactionLease();
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    private void ReleaseTransactionLease()
    {
        IDisposable? lease = Interlocked.Exchange(ref _transactionLease, null);
        lease?.Dispose();
    }
}

internal sealed partial class ClashDataPackageService
{
    private const int TransactionManifestVersion = 2;
    private const string TransactionDirectoryName = ".clashsharp-data-package-transaction";
    private const string TransactionLockFileName = ".clashsharp-data-package-transaction.lock";
    private const string TransactionManifestFileName = "manifest.json";
    private const string TransactionOwnerFileName = ".owner";
    private const string TransactionOwnerIdentity = "ClashSharp.DataPackageTransaction/1";
    private const string OperationOwnerFileName = ".operation-owner";
    private const int MaxTransactionManifestBytes = 16 * 1024 * 1024;
    private const int MaxTransactionRelativePathChars = 1024;
    private const long MaxTransactionBackupFileBytes = 64L * 1024 * 1024;
    private const long MaxTransactionBackupBytes = 256L * 1024 * 1024;
    private const int MaxTransactionCleanupEntries = (MaxPackageFileCount * 4) + 128;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions TransactionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private readonly Action<DataPackageTransactionCheckpoint>? _checkpoint;

    /// <summary>
    /// Resets persisted settings through the same durable generation boundary used by package imports.
    /// A crash before the reset generation is recorded rolls back; a crash afterwards completes it.
    /// </summary>
#if UNIT_TESTS
    public async Task ResetSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataPackageTransactionReceipt receipt = BeginResetSettings();
        try
        {
            await receipt.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await receipt.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Applies default settings while retaining a durable rollback generation.</summary>
    internal DataPackageTransactionReceipt BeginResetSettings()
    {
        return BeginResetSettingsCore(admissionLease: null);
    }
#endif

    /// <summary>Applies defaults under the caller's already-drained settings lease.</summary>
    internal DataPackageTransactionReceipt BeginResetSettingsAdmitted(
        MutationAdmissionLease admissionLease)
    {
        ArgumentNullException.ThrowIfNull(admissionLease);
        return BeginResetSettingsCore(admissionLease);
    }

    private DataPackageTransactionReceipt BeginResetSettingsCore(
        MutationAdmissionLease? admissionLease)
    {
        FileStream transactionLock = AcquireTransactionLock();
        bool leaseTransferred = false;
        try
        {
            ReconcilePendingTransactionUnderLock(admissionLease);

            Dictionary<string, string> previousSettings = CaptureSettings();
            Guid operationId = Guid.NewGuid();
            CreateOwnedOperationDirectory(operationId);
            DataPackageTransactionManifest manifest = new()
            {
                Version = TransactionManifestVersion,
                OperationId = operationId,
                Kind = DataPackageTransactionKind.ResetSettings,
                Phase = DataPackageTransactionPhase.ResetPrepared,
                PreviousSettings = previousSettings,
                DesiredSettings = null,
                Files = [],
            };

            PersistManifest(manifest);
            SignalCheckpoint(DataPackageTransactionCheckpoint.ManifestPersisted);
            try
            {
                WriteSettings(admissionLease, static settings => settings.ResetAllSettings());
                SignalCheckpoint(DataPackageTransactionCheckpoint.ResetMutationCompleted);

                manifest.DesiredSettings = CaptureSettings();
                manifest.Phase = DataPackageTransactionPhase.ResetSettingsApplied;
                PersistManifest(manifest);
                SignalCheckpoint(DataPackageTransactionCheckpoint.ResetSettingsApplied);
            }
            catch (Exception resetFailure) when (ShouldRecoverInProcess(resetFailure))
            {
                Exception? rollbackFailure = TryRollback(manifest, admissionLease);
                if (rollbackFailure is not null)
                {
                    throw new AggregateException(
                        "Settings reset failed and its durable snapshot could not be restored.",
                        resetFailure,
                        rollbackFailure);
                }

                throw;
            }

            DataPackageTransactionReceipt receipt = CreateReceipt(
                manifest.OperationId,
                transactionLock,
                admissionLease);
            leaseTransferred = true;
            return receipt;
        }
        finally
        {
            if (!leaseTransferred)
            {
                transactionLock.Dispose();
            }
        }
    }

    /// <summary>Completes or rolls back a retained package/reset transaction after an abnormal exit.</summary>
#if UNIT_TESTS
    public Task ReconcilePendingTransactionAsync(CancellationToken cancellationToken)
    {
        return ReconcilePendingTransactionCoreAsync(admissionLease: null, cancellationToken);
    }
#endif

    /// <summary>Reconciles a pending package transaction under an exclusive recovery lease.</summary>
    internal Task ReconcilePendingTransactionAdmittedAsync(
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admissionLease);
        return ReconcilePendingTransactionCoreAsync(admissionLease, cancellationToken);
    }

    private Task ReconcilePendingTransactionCoreAsync(
        MutationAdmissionLease? admissionLease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream transactionLock = AcquireTransactionLock();
        cancellationToken.ThrowIfCancellationRequested();
        ReconcilePendingTransactionUnderLock(admissionLease);
        return Task.CompletedTask;
    }

    private async Task<DataPackageTransactionReceipt> BeginValidatedImportAsync(
        IReadOnlyList<ImportFilePayload> files,
        IReadOnlyDictionary<string, string> importedSettings,
        MutationAdmissionLease? admissionLease,
        CancellationToken cancellationToken)
    {
        FileStream transactionLock = AcquireTransactionLock();
        bool leaseTransferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReconcilePendingTransactionUnderLock(admissionLease);

            Dictionary<string, string> previousSettings = CaptureSettings();
            Dictionary<string, string> desiredSettings = new(previousSettings, StringComparer.Ordinal);
            foreach ((string name, string value) in importedSettings)
            {
                desiredSettings[name] = value;
            }

            DataPackageTransactionManifest manifest = await PrepareImportAsync(
                files,
                previousSettings,
                desiredSettings,
                cancellationToken).ConfigureAwait(false);

            // The manifest is the forward decision. Cancellation after this cut would strand a half
            // generation in a live process, so the transaction now must reach forward or rollback.
            SignalCheckpoint(DataPackageTransactionCheckpoint.ManifestPersisted);
            try
            {
                ApplyImportForward(manifest, admissionLease);
            }
            catch (Exception importFailure) when (ShouldRecoverInProcess(importFailure))
            {
                Exception? rollbackFailure = TryRollback(manifest, admissionLease);
                if (rollbackFailure is not null)
                {
                    throw new AggregateException(
                        "Data package import failed and durable rollback could not restore one consistent generation.",
                        importFailure,
                        rollbackFailure);
                }

                throw;
            }

            DataPackageTransactionReceipt receipt = CreateReceipt(
                manifest.OperationId,
                transactionLock,
                admissionLease);
            leaseTransferred = true;
            return receipt;
        }
        finally
        {
            if (!leaseTransferred)
            {
                transactionLock.Dispose();
            }
        }
    }

    private async Task<DataPackageTransactionManifest> PrepareImportAsync(
        IReadOnlyList<ImportFilePayload> files,
        Dictionary<string, string> previousSettings,
        Dictionary<string, string> desiredSettings,
        CancellationToken cancellationToken)
    {
        Guid operationId = Guid.NewGuid();
        string operationDirectory = GetOperationDirectory(operationId);
        CreateOwnedOperationDirectory(operationId);

        List<DataPackageTransactionFile> transactionFiles = [];
        long totalBackupBytes = 0;
        bool manifestPersisted = false;
        try
        {
            for (int index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImportFilePayload file = files[index];
                string targetRelativePath = GetTargetRelativePath(file.TargetPath);
                string stagedRelativePath = GetExpectedStagedRelativePath(operationId, index);
                string stagedPath = ResolveTransactionRelativePath(stagedRelativePath);
                WriteBytesDurably(stagedPath, file.Content);

                bool hadOriginal = File.Exists(file.TargetPath);
                if (Directory.Exists(file.TargetPath))
                {
                    throw new InvalidDataException(
                        $"Data package target '{targetRelativePath}' is an existing directory.");
                }

                string? backupRelativePath = null;
                string? backupHash = null;
                long backupLength = 0;
                if (hadOriginal)
                {
                    EnsureImportTargetIsStillSafe(file.TargetPath);
                    long originalLength = new FileInfo(file.TargetPath).Length;
                    if (originalLength > MaxTransactionBackupFileBytes
                        || totalBackupBytes > MaxTransactionBackupBytes - originalLength)
                    {
                        throw new InvalidDataException(
                            "Data package recovery backup budget was exceeded.");
                    }

                    totalBackupBytes += originalLength;
                    backupRelativePath = GetExpectedBackupRelativePath(operationId, index);
                    string backupPath = ResolveTransactionRelativePath(backupRelativePath);
                    (backupHash, backupLength) = CopyFileDurably(file.TargetPath, backupPath);
                    if (backupLength != originalLength)
                    {
                        throw new IOException(
                            $"Import target '{targetRelativePath}' changed while its recovery backup was captured.");
                    }
                }

                transactionFiles.Add(new DataPackageTransactionFile
                {
                    TargetRelativePath = targetRelativePath,
                    StagedRelativePath = stagedRelativePath,
                    StagedSha256 = ComputeHash(file.Content),
                    StagedLength = file.Content.LongLength,
                    HadOriginal = hadOriginal,
                    BackupRelativePath = backupRelativePath,
                    BackupSha256 = backupHash,
                    BackupLength = backupLength,
                });
            }

            DataPackageTransactionManifest manifest = new()
            {
                Version = TransactionManifestVersion,
                OperationId = operationId,
                Kind = DataPackageTransactionKind.Import,
                Phase = DataPackageTransactionPhase.ImportPrepared,
                PreviousSettings = previousSettings,
                DesiredSettings = desiredSettings,
                Files = transactionFiles,
            };
            PersistManifest(manifest);
            manifestPersisted = true;
            return manifest;
        }
        finally
        {
            if (!manifestPersisted)
            {
                DeleteOwnedTransactionDirectory(operationId);
            }
        }
    }

    private void ReconcilePendingTransactionUnderLock(
        MutationAdmissionLease? admissionLease)
    {
        DataPackageTransactionManifest? manifest = LoadManifest();
        if (manifest is null)
        {
            CleanupOrphanedTransactionDirectories(activeOperationId: null);
            return;
        }

        ValidateManifest(manifest);
        if (manifest.Phase is DataPackageTransactionPhase.CommitCleanup
            or DataPackageTransactionPhase.RollbackCleanup)
        {
            CleanupTransaction(manifest);
            return;
        }

        if (manifest.Phase == DataPackageTransactionPhase.RollbackApplying
            || manifest.Kind == DataPackageTransactionKind.ResetSettings
                && manifest.Phase == DataPackageTransactionPhase.ResetPrepared)
        {
            Rollback(manifest, admissionLease);
            return;
        }

        try
        {
            if (manifest.Kind == DataPackageTransactionKind.Import)
            {
                ApplyImportForward(manifest, admissionLease);
            }
            else
            {
                ApplyResetForward(manifest, admissionLease);
            }
        }
        catch (Exception forwardFailure) when (ShouldRecoverInProcess(forwardFailure))
        {
            Exception? rollbackFailure = TryRollback(manifest, admissionLease);
            if (rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Pending data transaction could neither complete nor roll back.",
                    forwardFailure,
                    rollbackFailure);
            }

            return;
        }

        // Commit is a durable phase transition. Once recorded, cleanup can be retried but the
        // forward/rollback decision can no longer be reinterpreted.
        CommitTransaction(manifest);
    }

    private void ApplyImportForward(
        DataPackageTransactionManifest manifest,
        MutationAdmissionLease? admissionLease)
    {
        manifest.Phase = DataPackageTransactionPhase.ImportSettingsApplying;
        PersistManifest(manifest);
        ImportSettings(manifest.DesiredSettings!, admissionLease);
        SignalCheckpoint(DataPackageTransactionCheckpoint.ImportSettingsApplied);

        manifest.Phase = DataPackageTransactionPhase.ImportFilesApplying;
        PersistManifest(manifest);
        for (int index = 0; index < manifest.Files.Count; index++)
        {
            ApplyFileForward(manifest, manifest.Files[index], index);
            SignalCheckpoint(DataPackageTransactionCheckpoint.ImportFileApplied);
        }

        manifest.Phase = DataPackageTransactionPhase.ImportApplied;
        PersistManifest(manifest);
        SignalCheckpoint(DataPackageTransactionCheckpoint.TransactionApplied);
    }

    private void ApplyResetForward(
        DataPackageTransactionManifest manifest,
        MutationAdmissionLease? admissionLease)
    {
        if (manifest.DesiredSettings is null)
        {
            throw new InvalidDataException(
                "A reset transaction cannot complete forward before its desired generation is durable.");
        }

        RestoreSettings(manifest.DesiredSettings, admissionLease);
        manifest.Phase = DataPackageTransactionPhase.ResetSettingsApplied;
        PersistManifest(manifest);
    }

    private void ApplyFileForward(
        DataPackageTransactionManifest manifest,
        DataPackageTransactionFile file,
        int index)
    {
        string targetPath = ResolveManifestTargetPath(file.TargetRelativePath);
        EnsureImportTargetIsStillSafe(targetPath);
        if (FileMatches(targetPath, file.StagedLength, file.StagedSha256))
        {
            return;
        }

        if (File.Exists(targetPath))
        {
            bool matchesOriginal = file.HadOriginal
                && FileMatches(targetPath, file.BackupLength, file.BackupSha256!);
            if (!matchesOriginal)
            {
                throw new IOException(
                    $"Import target '{file.TargetRelativePath}' changed outside the pending transaction.");
            }
        }
        else if (Directory.Exists(targetPath))
        {
            throw new IOException(
                $"Import target '{file.TargetRelativePath}' became a directory.");
        }

        string stagedPath = ResolveTransactionRelativePath(file.StagedRelativePath);
        ReplaceTargetDurably(
            manifest.OperationId,
            index,
            stagedPath,
            file.StagedLength,
            file.StagedSha256,
            targetPath);
    }

    private Exception? TryRollback(
        DataPackageTransactionManifest manifest,
        MutationAdmissionLease? admissionLease)
    {
        try
        {
            manifest.Phase = DataPackageTransactionPhase.RollbackApplying;
            PersistManifest(manifest);
            SignalCheckpoint(DataPackageTransactionCheckpoint.RollbackPersisted);
            Rollback(manifest, admissionLease);
            return null;
        }
        catch (Exception rollbackFailure) when (ShouldRecoverInProcess(rollbackFailure))
        {
            return rollbackFailure;
        }
    }

    private void Rollback(
        DataPackageTransactionManifest manifest,
        MutationAdmissionLease? admissionLease)
    {
        RestoreSettings(manifest.PreviousSettings, admissionLease);
        for (int index = manifest.Files.Count - 1; index >= 0; index--)
        {
            RollbackFile(manifest, manifest.Files[index], index);
            SignalCheckpoint(DataPackageTransactionCheckpoint.RollbackFileApplied);
        }

        manifest.Phase = DataPackageTransactionPhase.RollbackCleanup;
        PersistManifest(manifest);
        CleanupTransaction(manifest);
    }

    private void RollbackFile(
        DataPackageTransactionManifest manifest,
        DataPackageTransactionFile file,
        int index)
    {
        string targetPath = ResolveManifestTargetPath(file.TargetRelativePath);
        EnsureImportTargetIsStillSafe(targetPath);
        if (file.HadOriginal)
        {
            if (FileMatches(targetPath, file.BackupLength, file.BackupSha256!))
            {
                return;
            }

            if (File.Exists(targetPath)
                && !FileMatches(targetPath, file.StagedLength, file.StagedSha256))
            {
                throw new IOException(
                    $"Import target '{file.TargetRelativePath}' changed outside the pending rollback.");
            }

            string backupPath = ResolveTransactionRelativePath(file.BackupRelativePath!);
            ReplaceTargetDurably(
                manifest.OperationId,
                index,
                backupPath,
                file.BackupLength,
                file.BackupSha256!,
                targetPath);
            return;
        }

        if (!File.Exists(targetPath))
        {
            if (Directory.Exists(targetPath))
            {
                throw new IOException(
                    $"Import target '{file.TargetRelativePath}' became a directory.");
            }

            return;
        }

        if (!FileMatches(targetPath, file.StagedLength, file.StagedSha256))
        {
            throw new IOException(
                $"Import target '{file.TargetRelativePath}' changed outside the pending rollback.");
        }

        File.Delete(targetPath);
    }

    private void ReplaceTargetDurably(
        Guid operationId,
        int index,
        string sourcePath,
        long expectedLength,
        string expectedHash,
        string targetPath)
    {
        if (!FileMatches(sourcePath, expectedLength, expectedHash))
        {
            throw new InvalidDataException("A data transaction recovery payload is missing or corrupted.");
        }

        string? targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new InvalidDataException("A data transaction target directory could not be resolved.");
        }

        Directory.CreateDirectory(targetDirectory);
        EnsureImportTargetIsStillSafe(targetPath);
        string temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.data-transaction-{operationId:N}-{index:D8}.tmp");
        EnsurePathWithinLocalData(temporaryPath);
        EnsureExistingPathIsNotReparsePoint(temporaryPath, "replacement sidecar");
        try
        {
            (string copiedHash, long copiedLength) = CopyFileDurably(sourcePath, temporaryPath);
            if (copiedLength != expectedLength
                || !StringComparer.Ordinal.Equals(copiedHash, expectedHash))
            {
                throw new IOException("A data transaction recovery payload changed while it was copied.");
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            if (!FileMatches(targetPath, expectedLength, expectedHash))
            {
                throw new IOException("A data transaction target did not match its committed generation.");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                EnsureNotReparsePoint(temporaryPath, "replacement sidecar");
                File.Delete(temporaryPath);
            }
        }
    }

    private DataPackageTransactionReceipt CreateReceipt(
        Guid operationId,
        IDisposable transactionLease,
        MutationAdmissionLease? admissionLease) =>
        new(operationId, transactionLease, (rollback, cancellationToken) =>
            CompleteRetainedTransactionUnderLockAsync(
                operationId,
                rollback,
                admissionLease,
                cancellationToken));

    private Task CompleteRetainedTransactionUnderLockAsync(
        Guid operationId,
        bool rollback,
        MutationAdmissionLease? admissionLease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataPackageTransactionManifest manifest = LoadManifest()
            ?? throw new InvalidOperationException("The retained data package transaction no longer exists.");
        ValidateManifest(manifest);
        if (manifest.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "A different data package transaction owns the retained recovery state.");
        }

        if (rollback)
        {
            if (manifest.Phase == DataPackageTransactionPhase.CommitCleanup)
            {
                throw new InvalidOperationException(
                    "A committed data package transaction can no longer be rolled back.");
            }

            if (manifest.Phase == DataPackageTransactionPhase.RollbackCleanup)
            {
                CleanupTransaction(manifest);
            }
            else
            {
                manifest.Phase = DataPackageTransactionPhase.RollbackApplying;
                PersistManifest(manifest);
                SignalCheckpoint(DataPackageTransactionCheckpoint.RollbackPersisted);
                Rollback(manifest, admissionLease);
            }
        }
        else
        {
            if (manifest.Phase is DataPackageTransactionPhase.RollbackApplying
                or DataPackageTransactionPhase.RollbackCleanup)
            {
                throw new InvalidOperationException(
                    "A rolling-back data package transaction can no longer be committed.");
            }

            if (manifest.Phase == DataPackageTransactionPhase.CommitCleanup)
            {
                CleanupTransaction(manifest);
            }
            else if (IsForwardApplied(manifest))
            {
                CommitTransaction(manifest);
            }
            else
            {
                throw new InvalidOperationException(
                    "The data package transaction has not reached its forward-applied phase.");
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsForwardApplied(DataPackageTransactionManifest manifest) =>
        manifest.Kind switch
        {
            DataPackageTransactionKind.Import =>
                manifest.Phase == DataPackageTransactionPhase.ImportApplied,
            DataPackageTransactionKind.ResetSettings =>
                manifest.Phase == DataPackageTransactionPhase.ResetSettingsApplied,
            _ => false,
        };

    private void CommitTransaction(DataPackageTransactionManifest manifest)
    {
        manifest.Phase = DataPackageTransactionPhase.CommitCleanup;
        PersistManifest(manifest);
        CleanupTransaction(manifest);
    }

    private void CleanupTransaction(DataPackageTransactionManifest manifest)
    {
        SignalCheckpoint(DataPackageTransactionCheckpoint.TransactionCleanupStarting);
        DeleteTransactionDirectoryForCleanup(manifest);
        SignalCheckpoint(DataPackageTransactionCheckpoint.TransactionPayloadCleanupCompleted);
        CleanupOrphanedTransactionDirectories(activeOperationId: null);
        string manifestPath = GetManifestPath();
        if (File.Exists(manifestPath))
        {
            EnsureNotReparsePoint(manifestPath, "transaction manifest");
            File.Delete(manifestPath);
        }
    }

    private void PersistManifest(DataPackageTransactionManifest manifest)
    {
        manifest.ManifestSha256 = ComputeManifestHash(manifest);
        ValidateManifest(manifest);
        string json = JsonSerializer.Serialize(manifest, TransactionJsonOptions);
        int encodedLength = StrictUtf8.GetByteCount(json) + 1;
        if (encodedLength > MaxTransactionManifestBytes)
        {
            throw new InvalidDataException("Data transaction manifest exceeds the size limit.");
        }

        EnsureTransactionRoot();
        string manifestPath = GetManifestPath();
        EnsureExistingPathIsNotReparsePoint(manifestPath, "transaction manifest");
        DurableAtomicFile.WriteText(manifestPath, json);
        EnsureNotReparsePoint(manifestPath, "transaction manifest");
    }

    private DataPackageTransactionManifest? LoadManifest()
    {
        string manifestPath = GetManifestPath();
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        EnsureTransactionRoot();
        EnsureNotReparsePoint(manifestPath, "transaction manifest");
        FileInfo manifestFile = new(manifestPath);
        if (manifestFile.Length <= 0 || manifestFile.Length > MaxTransactionManifestBytes)
        {
            throw new InvalidDataException("Data transaction manifest has an invalid size.");
        }

        byte[] bytes = File.ReadAllBytes(manifestPath);
        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Data transaction manifest is not valid UTF-8.", exception);
        }

        DataPackageTransactionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DataPackageTransactionManifest>(
                json,
                TransactionJsonOptions)
                ?? throw new InvalidDataException("Data transaction manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Data transaction manifest is invalid.", exception);
        }

        return manifest;
    }

    private void ValidateManifest(DataPackageTransactionManifest manifest)
    {
        if (manifest.Version != TransactionManifestVersion
            || manifest.OperationId == Guid.Empty
            || !IsSha256(manifest.ManifestSha256)
            || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(manifest.ManifestSha256),
                Convert.FromHexString(ComputeManifestHash(manifest)))
            || !Enum.IsDefined(manifest.Kind)
            || !Enum.IsDefined(manifest.Phase)
            || manifest.Files is null
            || manifest.Files.Count > MaxPackageFileCount)
        {
            throw new InvalidDataException("Data transaction manifest metadata is invalid.");
        }

        bool phaseMatchesKind = manifest.Kind switch
        {
            DataPackageTransactionKind.Import => manifest.Phase is
                DataPackageTransactionPhase.ImportPrepared
                or DataPackageTransactionPhase.ImportSettingsApplying
                or DataPackageTransactionPhase.ImportFilesApplying
                or DataPackageTransactionPhase.ImportApplied
                or DataPackageTransactionPhase.RollbackApplying
                or DataPackageTransactionPhase.CommitCleanup
                or DataPackageTransactionPhase.RollbackCleanup,
            DataPackageTransactionKind.ResetSettings => manifest.Phase is
                DataPackageTransactionPhase.ResetPrepared
                or DataPackageTransactionPhase.ResetSettingsApplied
                or DataPackageTransactionPhase.RollbackApplying
                or DataPackageTransactionPhase.CommitCleanup
                or DataPackageTransactionPhase.RollbackCleanup,
            _ => false,
        };
        if (!phaseMatchesKind
            || manifest.Kind == DataPackageTransactionKind.ResetSettings && manifest.Files.Count != 0)
        {
            throw new InvalidDataException("Data transaction manifest phase is invalid.");
        }

        manifest.PreviousSettings = ValidateSettingsGeneration(manifest.PreviousSettings);
        if (manifest.DesiredSettings is not null)
        {
            manifest.DesiredSettings = ValidateSettingsGeneration(manifest.DesiredSettings);
        }

        if (manifest.Kind == DataPackageTransactionKind.Import && manifest.DesiredSettings is null
            || manifest.Kind == DataPackageTransactionKind.ResetSettings
                && manifest.Phase is DataPackageTransactionPhase.ResetSettingsApplied
                    or DataPackageTransactionPhase.CommitCleanup
                && manifest.DesiredSettings is null)
        {
            throw new InvalidDataException("Data transaction manifest has no desired settings generation.");
        }

        HashSet<string> targetPaths = new(StringComparer.OrdinalIgnoreCase);
        long totalStagedBytes = 0;
        long totalBackupBytes = 0;
        for (int index = 0; index < manifest.Files.Count; index++)
        {
            DataPackageTransactionFile file = manifest.Files[index]
                ?? throw new InvalidDataException("Data transaction manifest has a null file target.");
            if (string.IsNullOrWhiteSpace(file.TargetRelativePath)
                || file.TargetRelativePath.Length > MaxTransactionRelativePathChars
                || file.StagedLength < 0
                || file.StagedLength > MaxPackageEntryBytes
                || totalStagedBytes > MaxPackageDecodedBytes - file.StagedLength
                || !IsSha256(file.StagedSha256)
                || !StringComparer.Ordinal.Equals(
                    file.StagedRelativePath,
                    GetExpectedStagedRelativePath(manifest.OperationId, index)))
            {
                throw new InvalidDataException("Data transaction manifest has an invalid staged file.");
            }

            string targetPath = ResolveManifestTargetPath(file.TargetRelativePath);
            string canonicalRelativePath = GetTargetRelativePath(targetPath);
            if (!StringComparer.Ordinal.Equals(canonicalRelativePath, file.TargetRelativePath)
                || !targetPaths.Add(targetPath)
                || IsTransactionInfrastructurePath(targetPath)
                || !IsProxyConfigurationImportPath(targetPath)
                || IsGeneratedRuntimeConfigPath(targetPath))
            {
                throw new InvalidDataException("Data transaction manifest has an unsafe target path.");
            }

            totalStagedBytes += file.StagedLength;

            _ = ResolveTransactionRelativePath(file.StagedRelativePath);
            if (file.HadOriginal)
            {
                if (file.BackupLength < 0
                    || file.BackupLength > MaxTransactionBackupFileBytes
                    || totalBackupBytes > MaxTransactionBackupBytes - file.BackupLength
                    || !IsSha256(file.BackupSha256)
                    || !StringComparer.Ordinal.Equals(
                        file.BackupRelativePath,
                        GetExpectedBackupRelativePath(manifest.OperationId, index)))
                {
                    throw new InvalidDataException("Data transaction manifest has an invalid backup file.");
                }

                _ = ResolveTransactionRelativePath(file.BackupRelativePath!);
                totalBackupBytes += file.BackupLength;
            }
            else if (file.BackupRelativePath is not null
                || file.BackupSha256 is not null
                || file.BackupLength != 0)
            {
                throw new InvalidDataException("Data transaction manifest has an unexpected backup file.");
            }
        }

        if (manifest.Phase is not DataPackageTransactionPhase.CommitCleanup
            and not DataPackageTransactionPhase.RollbackCleanup)
        {
            EnsureOperationDirectoryOwned(manifest.OperationId);
        }
    }

    private static string ComputeManifestHash(DataPackageTransactionManifest manifest)
    {
        string previousHash = manifest.ManifestSha256;
        try
        {
            manifest.ManifestSha256 = string.Empty;
            byte[] canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, TransactionJsonOptions);
            return Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        }
        finally
        {
            manifest.ManifestSha256 = previousHash;
        }
    }

    private static Dictionary<string, string> ValidateSettingsGeneration(
        Dictionary<string, string>? settings)
    {
        if (settings is null || settings.Count != SettingDescriptors.Length)
        {
            throw new InvalidDataException("Data transaction settings generation is incomplete.");
        }

        Dictionary<string, string> normalized = new(StringComparer.Ordinal);
        foreach (SettingDescriptor descriptor in SettingDescriptors)
        {
            if (!settings.TryGetValue(descriptor.Name, out string? value))
            {
                throw new InvalidDataException("Data transaction settings generation is incomplete.");
            }

            normalized.Add(descriptor.Name, descriptor.Normalize(value));
        }

        return normalized;
    }

    private FileStream AcquireTransactionLock()
    {
        Directory.CreateDirectory(_localDataDirectory);
        EnsureNotReparsePoint(_localDataDirectory, "local data directory");
        string lockPath = Path.Combine(_localDataDirectory, TransactionLockFileName);
        EnsureExistingPathIsNotReparsePoint(lockPath, "transaction lock");
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose | FileOptions.WriteThrough);
    }

    private void EnsureTransactionRoot()
    {
        string root = GetTransactionRoot();
        bool rootAlreadyExisted = Directory.Exists(root);
        EnsureExistingPathIsNotReparsePoint(root, "transaction root");
        Directory.CreateDirectory(root);
        EnsureNotReparsePoint(root, "transaction root");
        string ownerPath = Path.Combine(root, TransactionOwnerFileName);
        if (!File.Exists(ownerPath))
        {
            if (Directory.Exists(ownerPath)
                || rootAlreadyExisted && Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new InvalidDataException(
                    "The data transaction directory is not owned by Clash#.");
            }

            WriteBytesDurably(ownerPath, StrictUtf8.GetBytes(TransactionOwnerIdentity));
        }

        ValidateOwnerFile(ownerPath, TransactionOwnerIdentity, "transaction root owner");
    }

    private void CreateOwnedOperationDirectory(Guid operationId)
    {
        EnsureTransactionRoot();
        string operationDirectory = GetOperationDirectory(operationId);
        if (Directory.Exists(operationDirectory) || File.Exists(operationDirectory))
        {
            throw new InvalidDataException("The data transaction operation path already exists.");
        }

        Directory.CreateDirectory(operationDirectory);
        bool ownershipEstablished = false;
        try
        {
            EnsureNotReparsePoint(operationDirectory, "transaction staging directory");
            string ownerPath = Path.Combine(operationDirectory, OperationOwnerFileName);
            WriteBytesDurably(ownerPath, StrictUtf8.GetBytes(GetOperationOwnerIdentity(operationId)));
            EnsureOperationDirectoryOwned(operationId);
            ownershipEstablished = true;
        }
        finally
        {
            if (!ownershipEstablished && Directory.Exists(operationDirectory))
            {
                DeleteTransactionDirectory(operationDirectory);
            }
        }
    }

    private void EnsureOperationDirectoryOwned(Guid operationId)
    {
        string operationDirectory = GetOperationDirectory(operationId);
        EnsureNotReparsePoint(operationDirectory, "transaction operation directory");
        ValidateOwnerFile(
            Path.Combine(operationDirectory, OperationOwnerFileName),
            GetOperationOwnerIdentity(operationId),
            "transaction operation owner");
    }

    private static void ValidateOwnerFile(string path, string expectedIdentity, string description)
    {
        EnsureNotReparsePoint(path, description);
        FileInfo ownerFile = new(path);
        if (ownerFile.Length <= 0 || ownerFile.Length > 256)
        {
            throw new InvalidDataException($"Data package {description} is invalid.");
        }

        string identity;
        try
        {
            identity = StrictUtf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Data package {description} is invalid.", exception);
        }

        if (!StringComparer.Ordinal.Equals(identity, expectedIdentity))
        {
            throw new InvalidDataException($"Data package {description} is invalid.");
        }
    }

    private static string GetOperationOwnerIdentity(Guid operationId) =>
        $"{TransactionOwnerIdentity}/{operationId:N}";

    private string GetTransactionRoot()
    {
        string root = Path.GetFullPath(Path.Combine(_localDataDirectory, TransactionDirectoryName));
        EnsurePathWithinLocalData(root);
        return root;
    }

    private string GetManifestPath() => Path.Combine(GetTransactionRoot(), TransactionManifestFileName);

    private string GetOperationDirectory(Guid operationId) =>
        Path.Combine(GetTransactionRoot(), operationId.ToString("N"));

    private bool IsTransactionInfrastructurePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string lockPath = Path.GetFullPath(Path.Combine(_localDataDirectory, TransactionLockFileName));
        string transactionRoot = Path.GetFullPath(GetTransactionRoot());
        return StringComparer.OrdinalIgnoreCase.Equals(fullPath, lockPath)
            || StringComparer.OrdinalIgnoreCase.Equals(fullPath, transactionRoot)
            || fullPath.StartsWith(EnsureTrailingSeparator(transactionRoot), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExpectedStagedRelativePath(Guid operationId, int index) =>
        $"{operationId:N}/payload-{index:D8}.new";

    private static string GetExpectedBackupRelativePath(Guid operationId, int index) =>
        $"{operationId:N}/backup-{index:D8}.old";

    private string ResolveTransactionRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Length > MaxTransactionRelativePathChars
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Data transaction manifest has an unsafe recovery path.");
        }

        string root = EnsureTrailingSeparator(GetTransactionRoot());
        string fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Data transaction manifest has an unsafe recovery path.");
        }

        EnsurePathHasNoReparsePoints(Path.TrimEndingDirectorySeparator(root), fullPath);
        return fullPath;
    }

    private string ResolveManifestTargetPath(string relativePath)
    {
        string targetPath = ResolveImportFilePath(relativePath);
        EnsurePathWithinLocalData(targetPath);
        return targetPath;
    }

    private string GetTargetRelativePath(string targetPath)
    {
        EnsurePathWithinLocalData(targetPath);
        string relativePath = Path.GetRelativePath(_localDataDirectory, targetPath).Replace('\\', '/');
        if (relativePath.Length > MaxTransactionRelativePathChars)
        {
            throw new InvalidDataException("Data package target path exceeds the transaction limit.");
        }

        return relativePath;
    }

    private void EnsurePathWithinLocalData(string path)
    {
        string root = EnsureTrailingSeparator(Path.GetFullPath(_localDataDirectory));
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Data transaction path escapes the local data directory.");
        }
    }

    private void EnsureImportTargetIsStillSafe(string targetPath)
    {
        string root = EnsureTrailingSeparator(Path.GetFullPath(_localDataDirectory));
        EnsurePathWithinLocalData(targetPath);
        EnsureImportPathHasNoReparsePoints(root, targetPath);
    }

    private static void EnsurePathHasNoReparsePoints(string root, string targetPath)
    {
        string relativePath = Path.GetRelativePath(root, targetPath);
        string currentPath = root;
        foreach (string segment in relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            try
            {
                EnsureNotReparsePoint(currentPath, "transaction recovery path");
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }
    }

    private static void EnsureExistingPathIsNotReparsePoint(string path, string description)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            EnsureNotReparsePoint(path, description);
        }
    }

    private static void EnsureNotReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Data package {description} cannot be a reparse point.");
        }
    }

    private static void WriteBytesDurably(string path, byte[] content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidDataException("Data transaction staging directory could not be resolved.");
        }

        Directory.CreateDirectory(directory);
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static (string Hash, long Length) CopyFileDurably(string sourcePath, string destinationPath)
    {
        EnsureNotReparsePoint(sourcePath, "recovery source");
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidDataException("Data transaction destination directory could not be resolved.");
        }

        Directory.CreateDirectory(destinationDirectory);
        using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using FileStream destination = new(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        destination.Flush(flushToDisk: true);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    private static bool FileMatches(string path, long expectedLength, string expectedHash)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        EnsureNotReparsePoint(path, "transaction file");
        FileInfo file = new(path);
        if (file.Length != expectedLength)
        {
            return false;
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return StringComparer.Ordinal.Equals(actualHash, expectedHash);
    }

    private static string ComputeHash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void CleanupOrphanedTransactionDirectories(Guid? activeOperationId)
    {
        string root = GetTransactionRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        EnsureTransactionRoot();
        int entries = 0;
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (++entries > MaxTransactionCleanupEntries)
            {
                throw new InvalidDataException("Data transaction cleanup entry limit was exceeded.");
            }

            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                // Never traverse or delete an unproven orphan reparse point.
                continue;
            }

            string name = Path.GetFileName(directory);
            if (Guid.TryParseExact(name, "N", out Guid operationId)
                && operationId != activeOperationId)
            {
                string ownerPath = Path.Combine(directory, OperationOwnerFileName);
                if (File.Exists(ownerPath))
                {
                    try
                    {
                        EnsureOperationDirectoryOwned(operationId);
                        DeleteTransactionDirectory(directory);
                    }
                    catch (Exception exception) when (exception is
                        InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        // A foreign or damaged GUID directory has no valid ownership proof. Leave
                        // it untouched; it must not block recovery of the active manifest.
                    }
                }
            }
        }

        foreach (string file in Directory.EnumerateFiles(root))
        {
            if (++entries > MaxTransactionCleanupEntries)
            {
                throw new InvalidDataException("Data transaction cleanup entry limit was exceeded.");
            }

            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string name = Path.GetFileName(file);
            if (!StringComparer.OrdinalIgnoreCase.Equals(name, TransactionManifestFileName)
                && name.StartsWith($".{TransactionManifestFileName}.", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }
    }

    private void DeleteOwnedTransactionDirectory(Guid operationId)
    {
        string directory = GetOperationDirectory(operationId);
        if (!Directory.Exists(directory))
        {
            return;
        }

        EnsureOperationDirectoryOwned(operationId);
        DeleteTransactionDirectory(directory);
    }

    private void DeleteTransactionDirectoryForCleanup(DataPackageTransactionManifest manifest)
    {
        string directory = GetOperationDirectory(manifest.OperationId);
        if (!Directory.Exists(directory))
        {
            return;
        }

        string ownerPath = Path.Combine(directory, OperationOwnerFileName);
        if (File.Exists(ownerPath))
        {
            EnsureOperationDirectoryOwned(manifest.OperationId);
            DeleteTransactionDirectory(directory);
            return;
        }

        // A recursive directory deletion can remove the marker before another locked entry makes
        // it fail. In a terminal cleanup phase, the checksum-protected manifest still proves the
        // exact operation directory. Resume only when every survivor has an expected WAL name.
        EnsureNotReparsePoint(directory, "partially cleaned transaction directory");
        if (Directory.EnumerateDirectories(directory).Any())
        {
            throw new InvalidDataException(
                "A partially cleaned data transaction contains an unexpected directory.");
        }

        HashSet<string> expectedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            OperationOwnerFileName,
        };
        foreach (DataPackageTransactionFile file in manifest.Files)
        {
            expectedNames.Add(Path.GetFileName(file.StagedRelativePath));
            if (file.BackupRelativePath is not null)
            {
                expectedNames.Add(Path.GetFileName(file.BackupRelativePath));
            }
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            EnsureNotReparsePoint(file, "partially cleaned transaction file");
            if (!expectedNames.Contains(Path.GetFileName(file)))
            {
                throw new InvalidDataException(
                    "A partially cleaned data transaction contains an unexpected file.");
            }
        }

        DeleteTransactionDirectory(directory);
    }

    private static void DeleteTransactionDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        int entries = 0;
        Stack<string> pending = new();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            EnsureNotReparsePoint(current, "transaction cleanup directory");
            foreach (string file in Directory.EnumerateFiles(current))
            {
                if (++entries > MaxTransactionCleanupEntries)
                {
                    throw new InvalidDataException("Data transaction cleanup entry limit was exceeded.");
                }

                EnsureNotReparsePoint(file, "transaction cleanup file");
            }

            foreach (string child in Directory.EnumerateDirectories(current))
            {
                if (++entries > MaxTransactionCleanupEntries)
                {
                    throw new InvalidDataException("Data transaction cleanup entry limit was exceeded.");
                }

                EnsureNotReparsePoint(child, "transaction cleanup directory");
                pending.Push(child);
            }
        }

        Directory.Delete(directory, recursive: true);
    }

    private void SignalCheckpoint(DataPackageTransactionCheckpoint checkpoint) => _checkpoint?.Invoke(checkpoint);

    private static bool ShouldRecoverInProcess(Exception exception) =>
        exception is not DataPackageSimulatedCrashException
        && !ExceptionGraphClassifier.IsProcessFatal(exception);

    private enum DataPackageTransactionKind
    {
        Import = 0,
        ResetSettings = 1,
    }

    private enum DataPackageTransactionPhase
    {
        ImportPrepared = 0,
        ImportSettingsApplying = 1,
        ImportFilesApplying = 2,
        ImportApplied = 3,
        ResetPrepared = 4,
        ResetSettingsApplied = 5,
        RollbackApplying = 6,
        CommitCleanup = 7,
        RollbackCleanup = 8,
    }

    private sealed class DataPackageTransactionManifest
    {
        public int Version { get; set; }

        public string ManifestSha256 { get; set; } = string.Empty;

        public Guid OperationId { get; set; }

        public DataPackageTransactionKind Kind { get; set; }

        public DataPackageTransactionPhase Phase { get; set; }

        public Dictionary<string, string> PreviousSettings { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, string>? DesiredSettings { get; set; }

        public List<DataPackageTransactionFile> Files { get; set; } = [];
    }

    private sealed class DataPackageTransactionFile
    {
        public string TargetRelativePath { get; set; } = string.Empty;

        public string StagedRelativePath { get; set; } = string.Empty;

        public string StagedSha256 { get; set; } = string.Empty;

        public long StagedLength { get; set; }

        public bool HadOriginal { get; set; }

        public string? BackupRelativePath { get; set; }

        public string? BackupSha256 { get; set; }

        public long BackupLength { get; set; }
    }
}

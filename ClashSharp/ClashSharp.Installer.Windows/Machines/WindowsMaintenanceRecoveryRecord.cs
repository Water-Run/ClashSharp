using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal enum WindowsMaintenanceRecoveryStage
{
    Captured,
    ContinueCandidate,
    PreserveOriginal,
    OriginalVerified,
    OriginalClearReady,
    CandidateClearReady,
}

/// <summary>
/// Private write-ahead recovery state. Captured alone admits no machine effects. Original evidence
/// is immutable for the transaction, including an explicit unavailable result for damaged installs.
/// Clear-ready states retain enough evidence to reconcile a lost public-clear acknowledgement.
/// </summary>
internal sealed class WindowsMaintenanceRecoveryRecord
{
    internal const int CurrentSchema = 1;
    internal const int MaximumDocumentBytes = 256 * 1024;
    internal const string FileName = "maintenance-recovery-v1.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 10,
    };

    [JsonConstructor]
    public WindowsMaintenanceRecoveryRecord(int schema, InstallerTransactionJournal intent,
        WindowsMaintenanceOriginalBaseline? original, string? unavailableDiagnostic,
        WindowsMaintenanceRecoveryStage stage, int generation, InstallerTransactionSnapshot? restorationSource)
    {
        Schema = schema;
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        Original = original;
        UnavailableDiagnostic = unavailableDiagnostic;
        Stage = stage;
        Generation = generation;
        RestorationSource = restorationSource;
        Validate();
    }

    public int Schema { get; }
    public InstallerTransactionJournal Intent { get; }
    public WindowsMaintenanceOriginalBaseline? Original { get; }
    public string? UnavailableDiagnostic { get; }
    public WindowsMaintenanceRecoveryStage Stage { get; }
    public int Generation { get; }
    public InstallerTransactionSnapshot? RestorationSource { get; }

    internal static WindowsMaintenanceRecoveryRecord Capture(InstallerTransactionJournal intent,
        WindowsMaintenanceOriginalBaseline? original, string? unavailableDiagnostic) =>
        new(CurrentSchema, intent, original, unavailableDiagnostic, WindowsMaintenanceRecoveryStage.Captured, 1, null);

    internal bool CanRetire => Stage is WindowsMaintenanceRecoveryStage.Captured
        or WindowsMaintenanceRecoveryStage.OriginalClearReady or WindowsMaintenanceRecoveryStage.CandidateClearReady;

    internal InstallerTransactionSnapshot RestoredTerminal() => RestorationSource is { } source
        ? InstallerTransactionSnapshot.Create(source.Journal.TransitionTo(InstallerTransactionPhase.OriginalRestored))
        : throw Failure("restoration_source_missing");

    internal void RequireTransaction(InstallerTransactionSnapshot state)
    {
        state.Validate();
        InstallerTransactionJournal initial = state.Journal with { Phase = InstallerTransactionPhase.Prepared, Generation = 1 };
        if (initial != Intent) { throw Failure("transaction_mismatch"); }
    }

    internal WindowsMaintenanceRecoveryRecord Continue(InstallerTransactionSnapshot current)
    {
        RequireTransaction(current);
        if (current.Journal.Phase is not (InstallerTransactionPhase.Prepared or InstallerTransactionPhase.MachineReserved
                or InstallerTransactionPhase.PackageCommitted or InstallerTransactionPhase.MachineCommitted)
            || Stage is WindowsMaintenanceRecoveryStage.OriginalClearReady or WindowsMaintenanceRecoveryStage.CandidateClearReady)
        {
            throw Failure("transition_invalid");
        }
        return Stage == WindowsMaintenanceRecoveryStage.ContinueCandidate ? this
            : Next(WindowsMaintenanceRecoveryStage.ContinueCandidate, null);
    }

    internal WindowsMaintenanceRecoveryRecord Preserve(InstallerTransactionSnapshot current)
    {
        RequireTransaction(current);
        if (Original is null) { throw Failure("original_unavailable"); }
        if (current.Journal.Phase is not (InstallerTransactionPhase.Prepared or InstallerTransactionPhase.MachineReserved)
            || Stage is WindowsMaintenanceRecoveryStage.OriginalClearReady or WindowsMaintenanceRecoveryStage.CandidateClearReady)
        {
            throw Failure("transition_invalid");
        }
        if (Stage is WindowsMaintenanceRecoveryStage.PreserveOriginal or WindowsMaintenanceRecoveryStage.OriginalVerified)
        {
            if (RestorationSource != current) { throw Failure("restoration_source_mismatch"); }
            return this;
        }
        return Next(WindowsMaintenanceRecoveryStage.PreserveOriginal, current);
    }

    internal WindowsMaintenanceRecoveryRecord VerifyOriginal()
    {
        if (Stage == WindowsMaintenanceRecoveryStage.OriginalVerified) { return this; }
        if (Stage != WindowsMaintenanceRecoveryStage.PreserveOriginal) { throw Failure("transition_invalid"); }
        return Next(WindowsMaintenanceRecoveryStage.OriginalVerified, RestorationSource);
    }

    internal WindowsMaintenanceRecoveryRecord PrepareOriginalClear(InstallerTransactionSnapshot terminal)
    {
        RequireTransaction(terminal);
        if (terminal != RestoredTerminal()
            || Stage is not (WindowsMaintenanceRecoveryStage.OriginalVerified or WindowsMaintenanceRecoveryStage.OriginalClearReady))
        {
            throw Failure("transition_invalid");
        }
        return Stage == WindowsMaintenanceRecoveryStage.OriginalClearReady ? this
            : Next(WindowsMaintenanceRecoveryStage.OriginalClearReady, RestorationSource);
    }

    internal WindowsMaintenanceRecoveryRecord PrepareCandidateClear(InstallerTransactionSnapshot terminal)
    {
        RequireTransaction(terminal);
        if (terminal.Journal.Phase != InstallerTransactionPhase.Verified
            || Stage == WindowsMaintenanceRecoveryStage.OriginalClearReady)
        {
            throw Failure("transition_invalid");
        }
        return Stage == WindowsMaintenanceRecoveryStage.CandidateClearReady ? this
            : Next(WindowsMaintenanceRecoveryStage.CandidateClearReady, null);
    }

    private WindowsMaintenanceRecoveryRecord Next(WindowsMaintenanceRecoveryStage stage, InstallerTransactionSnapshot? source) =>
        new(Schema, Intent, Original, UnavailableDiagnostic, stage, checked(Generation + 1), source);

    internal void RequireSuccessorOf(WindowsMaintenanceRecoveryRecord previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (Generation != previous.Generation + 1 || Intent != previous.Intent
            || UnavailableDiagnostic != previous.UnavailableDiagnostic || !SameOriginal(Original, previous.Original))
        {
            throw Failure("write_conflict");
        }
        bool valid = Stage switch
        {
            WindowsMaintenanceRecoveryStage.ContinueCandidate => previous.Stage is WindowsMaintenanceRecoveryStage.Captured
                or WindowsMaintenanceRecoveryStage.PreserveOriginal or WindowsMaintenanceRecoveryStage.OriginalVerified,
            WindowsMaintenanceRecoveryStage.PreserveOriginal => previous.Stage is WindowsMaintenanceRecoveryStage.Captured
                or WindowsMaintenanceRecoveryStage.ContinueCandidate,
            WindowsMaintenanceRecoveryStage.OriginalVerified => previous.Stage == WindowsMaintenanceRecoveryStage.PreserveOriginal
                && RestorationSource == previous.RestorationSource,
            WindowsMaintenanceRecoveryStage.OriginalClearReady => previous.Stage == WindowsMaintenanceRecoveryStage.OriginalVerified
                && RestorationSource == previous.RestorationSource,
            WindowsMaintenanceRecoveryStage.CandidateClearReady => previous.Stage is WindowsMaintenanceRecoveryStage.Captured
                or WindowsMaintenanceRecoveryStage.ContinueCandidate or WindowsMaintenanceRecoveryStage.PreserveOriginal
                or WindowsMaintenanceRecoveryStage.OriginalVerified,
            _ => false,
        };
        if (!valid) { throw Failure("transition_invalid"); }
    }

    internal byte[] Serialize()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (bytes.Length > MaximumDocumentBytes) { CryptographicOperations.ZeroMemory(bytes); throw Failure("size_invalid"); }
        return bytes;
    }

    internal static WindowsMaintenanceRecoveryRecord Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumDocumentBytes) { throw Failure("size_invalid"); }
        try
        {
            WindowsMaintenanceRecoveryRecord record = JsonSerializer.Deserialize<WindowsMaintenanceRecoveryRecord>(bytes, Options)
                ?? throw Failure("invalid");
            byte[] canonical = record.Serialize();
            try { if (!bytes.SequenceEqual(canonical)) { throw Failure("noncanonical"); } }
            finally { CryptographicOperations.ZeroMemory(canonical); }
            return record;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InstallerProtocolException("installer.recovery.record_invalid", exception);
        }
    }

    private void Validate()
    {
        Intent.Validate();
        if (Schema != CurrentSchema || Intent.Operation != InstallerOperation.Repair || Intent.AllowReassociation
            || Intent.Phase != InstallerTransactionPhase.Prepared || !Enum.IsDefined(Stage)
            || Generation is < 1 or > 1_000_000 || (Stage == WindowsMaintenanceRecoveryStage.Captured) != (Generation == 1)
            || (Original is null) == (UnavailableDiagnostic is null))
        {
            throw Failure("invalid");
        }
        if (UnavailableDiagnostic is not null) { InstallerProtocolValidation.ValidateDiagnosticCode(UnavailableDiagnostic); }
        if (Original is not null && Original.Service.Intent != Intent) { throw Failure("baseline_mismatch"); }
        bool restoring = Stage is WindowsMaintenanceRecoveryStage.PreserveOriginal or WindowsMaintenanceRecoveryStage.OriginalVerified
            or WindowsMaintenanceRecoveryStage.OriginalClearReady;
        int minimumGeneration = Stage switch
        {
            WindowsMaintenanceRecoveryStage.Captured => 1,
            WindowsMaintenanceRecoveryStage.ContinueCandidate or WindowsMaintenanceRecoveryStage.PreserveOriginal
                or WindowsMaintenanceRecoveryStage.CandidateClearReady => 2,
            WindowsMaintenanceRecoveryStage.OriginalVerified => 3,
            WindowsMaintenanceRecoveryStage.OriginalClearReady => 4,
            _ => int.MaxValue,
        };
        if (Generation < minimumGeneration) { throw Failure("invalid"); }
        if (restoring)
        {
            if (Original is null || RestorationSource is null) { throw Failure("invalid"); }
            RequireTransaction(RestorationSource);
            if (RestorationSource.Journal.Phase is not (InstallerTransactionPhase.Prepared or InstallerTransactionPhase.MachineReserved)) { throw Failure("invalid"); }
        }
        else if (RestorationSource is not null) { throw Failure("invalid"); }
    }

    private static bool SameOriginal(WindowsMaintenanceOriginalBaseline? left, WindowsMaintenanceOriginalBaseline? right)
    {
        if (left is null || right is null) { return left is null && right is null; }
        byte[] first = left.Serialize();
        byte[]? second = null;
        try { second = right.Serialize(); return first.AsSpan().SequenceEqual(second); }
        finally { CryptographicOperations.ZeroMemory(first); if (second is not null) { CryptographicOperations.ZeroMemory(second); } }
    }

    private static InstallerProtocolException Failure(string suffix) => new("installer.recovery.record_" + suffix);
}

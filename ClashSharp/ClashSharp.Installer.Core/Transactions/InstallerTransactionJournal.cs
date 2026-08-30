using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Transactions;

/// <summary>Defines the strict, bounded recovery protocol shared by UI and elevated helper.</summary>
public sealed record InstallerTransactionJournal(
    int Schema,
    string TransactionId,
    InstallerOperation Operation,
    string TargetSid,
    bool AllowReassociation,
    string ExpectedPackageVersion,
    string InstallerPayloadSha256,
    InstallerTransactionPhase Phase,
    int Generation)
{
    /// <summary>Gets the current C# installer transaction schema.</summary>
    public const int CurrentSchema = 2;

    /// <summary>Creates a new prepared journal bound to an exact release.</summary>
    /// <param name="request">Validated installer request.</param>
    /// <returns>A first-generation prepared journal.</returns>
    public static InstallerTransactionJournal Create(InstallerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        InstallerTransactionJournal journal = new(
            CurrentSchema,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            request.Operation,
            request.TargetSid,
            request.AllowReassociation,
            request.ExpectedPackageVersion,
            request.InstallerPayloadSha256,
            InstallerTransactionPhase.Prepared,
            1);
        journal.Validate();
        return journal;
    }

    /// <summary>Validates the full document and all canonical fields.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema)
        {
            throw new InstallerProtocolException("installer.transaction.schema_invalid");
        }

        InstallerProtocolValidation.ValidateLowerHex256(
            TransactionId,
            "installer.transaction.id_invalid");
        new InstallerRequest(
            Operation,
            TargetSid,
            AllowReassociation,
            ExpectedPackageVersion,
            InstallerPayloadSha256).Validate();
        if (!Enum.IsDefined(Phase))
        {
            throw new InstallerProtocolException("installer.transaction.phase_invalid");
        }

        int expectedGeneration = (Operation, Phase) switch
        {
            (_, InstallerTransactionPhase.Prepared) => 1,
            (InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.MachineReserved) => 2,
            (InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.PackageCommitted) => 3,
            (InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.MachineCommitted) => 4,
            (InstallerOperation.Uninstall,
                InstallerTransactionPhase.MachineRemovalAuthorized) => 2,
            (InstallerOperation.Uninstall, InstallerTransactionPhase.MachineCommitted) => 3,
            (InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted) => 4,
            (InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.Verified) => 5,
            (InstallerOperation.Uninstall, InstallerTransactionPhase.Verified) => 5,
            _ => 0,
        };
        if (Generation != expectedGeneration)
        {
            throw new InstallerProtocolException("installer.transaction.generation_invalid");
        }
    }

    /// <summary>Returns whether this journal is the only transaction an exact request may resume.</summary>
    /// <param name="request">Candidate request.</param>
    /// <returns><see langword="true"/> only for an exact immutable identity match.</returns>
    public bool Matches(InstallerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        Validate();
        return Operation == request.Operation
            && string.Equals(TargetSid, request.TargetSid, StringComparison.Ordinal)
            && AllowReassociation == request.AllowReassociation
            && string.Equals(ExpectedPackageVersion, request.ExpectedPackageVersion, StringComparison.Ordinal)
            && string.Equals(InstallerPayloadSha256, request.InstallerPayloadSha256, StringComparison.Ordinal);
    }

    /// <summary>Advances one operation-specific durable phase, accepting an idempotent replay.</summary>
    /// <param name="next">Requested next phase.</param>
    /// <returns>The unchanged or next-generation journal.</returns>
    public InstallerTransactionJournal TransitionTo(InstallerTransactionPhase next)
    {
        Validate();
        if (next == Phase)
        {
            return this;
        }

        bool allowed = Operation switch
        {
            InstallerOperation.Install or InstallerOperation.Repair =>
                Phase == InstallerTransactionPhase.Prepared && next == InstallerTransactionPhase.MachineReserved
                || Phase == InstallerTransactionPhase.MachineReserved && next == InstallerTransactionPhase.PackageCommitted
                || Phase == InstallerTransactionPhase.PackageCommitted && next == InstallerTransactionPhase.MachineCommitted
                || Phase == InstallerTransactionPhase.MachineCommitted && next == InstallerTransactionPhase.Verified,
            InstallerOperation.Uninstall =>
                Phase == InstallerTransactionPhase.Prepared
                    && next == InstallerTransactionPhase.MachineRemovalAuthorized
                || Phase == InstallerTransactionPhase.MachineRemovalAuthorized
                    && next == InstallerTransactionPhase.MachineCommitted
                || Phase == InstallerTransactionPhase.MachineCommitted && next == InstallerTransactionPhase.PackageCommitted
                || Phase == InstallerTransactionPhase.PackageCommitted && next == InstallerTransactionPhase.Verified,
            _ => false,
        };
        if (!allowed)
        {
            throw new InstallerProtocolException("installer.transaction.phase_transition_invalid");
        }

        InstallerTransactionJournal advanced = this with
        {
            Phase = next,
            Generation = Generation + 1,
        };
        advanced.Validate();
        return advanced;
    }
}

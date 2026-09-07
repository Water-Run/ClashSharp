using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Retirement;

/// <summary>
/// Exchanges only canonical public uninstall snapshots on the authenticated dedicated pipe.
/// The first parent frame proposes Prepared; the helper returns its own new or recovered state.
/// Private certificate evidence and the shared owner's credentials never enter this protocol.
/// </summary>
public static class InstallerRetiredUninstallProtocol
{
    /// <summary>Validates that a snapshot is exclusively an account-copy uninstall.</summary>
    public static void Validate(InstallerTransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        if (snapshot.Journal.Operation != InstallerOperation.Uninstall || snapshot.Journal.AllowReassociation)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.operation_invalid");
        }
    }

    /// <summary>Checks the exact authenticated target and candidate without trusting the proposed transaction ID.</summary>
    public static void ValidateAgainst(InstallerTransactionSnapshot snapshot, InstallerRetiredUninstallBootstrap bootstrap,
        InstallerRequest expectedRequest)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(expectedRequest);
        Validate(snapshot);
        bootstrap.Validate();
        expectedRequest.Validate();
        if (expectedRequest.Operation != InstallerOperation.Uninstall || expectedRequest.AllowReassociation
            || expectedRequest.InstallerPayloadSha256 != bootstrap.InstallerPayloadSha256
            || !snapshot.Journal.Matches(expectedRequest))
        {
            throw new InstallerProtocolException("installer.retired_uninstall.candidate_mismatch");
        }
    }

    /// <summary>Chooses the first legal authority command for the helper's recovered durable phase.</summary>
    public static InstallerMachineHelperInvocation FirstInvocation(InstallerTransactionSnapshot snapshot)
    {
        Validate(snapshot);
        InstallerMachineHelperVerb verb = snapshot.Journal.Phase switch
        {
            InstallerTransactionPhase.Prepared => InstallerMachineHelperVerb.Prepare,
            InstallerTransactionPhase.MachineRemovalAuthorized => InstallerMachineHelperVerb.Remove,
            InstallerTransactionPhase.MachineCommitted => InstallerMachineHelperVerb.CommitPackage,
            InstallerTransactionPhase.PackageCommitted or InstallerTransactionPhase.Verified => InstallerMachineHelperVerb.Verify,
            _ => throw new InstallerProtocolException("installer.retired_uninstall.phase_invalid"),
        };
        return InstallerMachineHelperInvocation.Create(verb, snapshot);
    }

    /// <summary>Writes one bounded canonical public snapshot without closing the caller's stream.</summary>
    public static Task WriteAsync(Stream stream, InstallerTransactionSnapshot snapshot, CancellationToken cancellationToken)
    {
        Validate(snapshot);
        return InstallerMachineHelperFraming.WriteFrameAsync(
            stream, InstallerTransactionCodec.Serialize(snapshot.Journal), cancellationToken);
    }

    /// <summary>Reads exactly one frame and rejects noncanonical or unrelated transaction documents.</summary>
    public static async Task<InstallerTransactionSnapshot> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = await InstallerMachineHelperFraming.ReadFrameAsync(
            stream, InstallerTransactionCodec.MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
        return ParseSnapshot(bytes);
    }

    /// <summary>Writes a bounded stable preparation refusal without exposing helper-private evidence.</summary>
    public static Task WriteFailureAsync(Stream stream, string diagnosticCode, CancellationToken cancellationToken)
    {
        InstallerProtocolValidation.ValidateDiagnosticCode(diagnosticCode);
        return InstallerMachineHelperFraming.WriteFrameAsync(stream, FailureBytes(diagnosticCode), cancellationToken);
    }

    /// <summary>Reads authoritative readiness or a canonical stable refusal from the authenticated helper.</summary>
    public static async Task<InstallerTransactionSnapshot> ReadReadyAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = await InstallerMachineHelperFraming.ReadFrameAsync(
            stream, InstallerTransactionCodec.MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
        string? failure = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Count() == 1
                && document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
            {
                failure = error.GetString();
                InstallerProtocolValidation.ValidateDiagnosticCode(failure!);
                if (!CryptographicOperations.FixedTimeEquals(bytes, FailureBytes(failure!)))
                {
                    throw new InstallerProtocolException("installer.retired_uninstall.document_invalid");
                }
            }
        }
        catch (JsonException)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.document_invalid");
        }
        if (failure is not null)
        {
            throw new InstallerProtocolException(failure);
        }
        return ParseSnapshot(bytes);
    }

    private static byte[] FailureBytes(string code) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["error"] = code });

    private static InstallerTransactionSnapshot ParseSnapshot(byte[] bytes)
    {
        InstallerTransactionJournal journal = InstallerTransactionCodec.Parse(bytes);
        if (!CryptographicOperations.FixedTimeEquals(bytes, InstallerTransactionCodec.Serialize(journal)))
        {
            throw new InstallerProtocolException("installer.retired_uninstall.document_invalid");
        }
        InstallerTransactionSnapshot snapshot = InstallerTransactionSnapshot.Create(journal);
        Validate(snapshot);
        return snapshot;
    }
}

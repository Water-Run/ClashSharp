using System.Text;
using System.Text.Json.Nodes;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerDirectoryCleanupReportTests
{
    [Fact]
    public void ReportCopiesOrdersAndProtectsItsCompleteObservations()
    {
        InstallerDirectoryCleanupEntry[] input = Entries().Reverse().ToArray();
        var report = new InstallerDirectoryCleanupReport(input);
        input[0] = new(InstallerDirectoryRole.ProgramFilesProduct, InstallerDirectoryCleanupDisposition.RetainedNonEmpty);

        Assert.Equal(Entries(), report.Entries);
        Assert.Equal(new InstallerDirectoryCleanupReport(Entries()), report);
        Assert.False(report.HasRetained);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<InstallerDirectoryCleanupEntry>)report.Entries)[0] = input[0]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("role")]
    [InlineData("disposition")]
    [InlineData("null")]
    public void InvalidOrIncompleteObservationsCannotBecomeReports(string mutation)
    {
        List<InstallerDirectoryCleanupEntry> entries = Entries().ToList();
        switch (mutation)
        {
            case "missing": entries.RemoveAt(0); break;
            case "extra": entries.Add(entries[0]); break;
            case "duplicate": entries[1] = entries[0]; break;
            case "role": entries[0] = entries[0] with { Role = (InstallerDirectoryRole)6 }; break;
            case "disposition": entries[0] = entries[0] with { Disposition = (InstallerDirectoryCleanupDisposition)4 }; break;
            case "null": entries[0] = null!; break;
        }
        Assert.Equal("installer.directory_cleanup.report_invalid", Assert.Throws<InstallerProtocolException>(
            () => new InstallerDirectoryCleanupReport(entries)).DiagnosticCode);
    }

    [Fact]
    public void UnboundedInputIsRejectedAfterTheSeventhObservation()
    {
        int yielded = 0;
        IEnumerable<InstallerDirectoryCleanupEntry> Input()
        {
            while (true)
            {
                yielded++;
                yield return new(InstallerDirectoryRole.ProgramFilesProduct, InstallerDirectoryCleanupDisposition.Missing);
            }
        }
        Assert.Throws<InstallerProtocolException>(() => new InstallerDirectoryCleanupReport(Input()));
        Assert.Equal(7, yielded);
    }

    [Theory]
    [InlineData(InstallerDirectoryCleanupDisposition.Missing, false)]
    [InlineData(InstallerDirectoryCleanupDisposition.Deleted, false)]
    [InlineData(InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership, true)]
    [InlineData(InstallerDirectoryCleanupDisposition.RetainedNonEmpty, true)]
    public void CanonicalHelperReceiptPreservesEveryObservationAndItsJournalBinding(
        InstallerDirectoryCleanupDisposition disposition, bool retained)
    {
        InstallerMachineHelperCommand command = Command(InstallerOperation.Uninstall);
        InstallerMachineHelperResult expected = InstallerMachineHelperResult.Succeeded(command, command.ToDurableState())
            with
        { DirectoryCleanupReport = new InstallerDirectoryCleanupReport(Entries(disposition)) };

        byte[] bytes = InstallerMachineHelperResultCodec.Serialize(expected);
        InstallerMachineHelperResult actual = InstallerMachineHelperResultCodec.Parse(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(command.ToDurableState(), actual.ValidateAgainst(command));
        Assert.Equal(retained, actual.DirectoryCleanupReport!.HasRetained);
        Assert.True(bytes.Length <= InstallerMachineHelperResultCodec.MaximumResultBytes);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public void OtherOperationsCannotCarryDirectoryCleanup(InstallerOperation operation)
    {
        InstallerMachineHelperCommand command = Command(operation);
        InstallerMachineHelperResult result = InstallerMachineHelperResult.Succeeded(command, command.ToDurableState())
            with
        { DirectoryCleanupReport = new InstallerDirectoryCleanupReport(Entries()) };
        Assert.Equal("installer.directory_cleanup.result_binding_invalid",
            Assert.Throws<InstallerProtocolException>(() => result.ValidateAgainst(command)).DiagnosticCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAndNonClearRepliesCannotCarryDirectoryCleanup(bool failure)
    {
        InstallerMachineHelperCommand command = Command(InstallerOperation.Uninstall,
            failure ? InstallerMachineHelperVerb.Clear : InstallerMachineHelperVerb.Verify);
        InstallerMachineHelperResult result = (failure
            ? InstallerMachineHelperResult.Failed(command, "installer.test.failed")
            : InstallerMachineHelperResult.Succeeded(command, command.GetExpectedSuccessfulState()))
            with
        { DirectoryCleanupReport = new InstallerDirectoryCleanupReport(Entries()) };
        Assert.Equal("installer.directory_cleanup.result_binding_invalid",
            Assert.Throws<InstallerProtocolException>(() => result.Validate()).DiagnosticCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate-role")]
    [InlineData("unknown-role")]
    [InlineData("unknown-disposition")]
    [InlineData("unknown-property")]
    [InlineData("wrong-type")]
    [InlineData("nested")]
    [InlineData("null")]
    [InlineData("reordered")]
    public void NoncanonicalOrMalformedCleanupFramesAreRejected(string mutation)
    {
        JsonObject root = JsonNode.Parse(CanonicalJson())!.AsObject();
        JsonArray entries = root["directoryCleanup"]!.AsArray();
        switch (mutation)
        {
            case "missing": entries.RemoveAt(0); break;
            case "extra": entries.Add(entries[0]!.DeepClone()); break;
            case "duplicate-role": entries[1]!["role"] = entries[0]!["role"]!.GetValue<string>(); break;
            case "unknown-role": entries[0]!["role"] = "user-chosen-path"; break;
            case "unknown-disposition": entries[0]!["disposition"] = "ignored-error"; break;
            case "unknown-property": entries[0]!["path"] = "C:\\foreign"; break;
            case "wrong-type": entries[0]!["role"] = 0; break;
            case "nested": entries[0]!["role"] = new JsonObject { ["path"] = "foreign" }; break;
            case "null": root["directoryCleanup"] = null; break;
            case "reordered":
                JsonNode first = entries[0]!.DeepClone();
                entries[0] = entries[1]!.DeepClone();
                entries[1] = first;
                break;
        }
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerMachineHelperResultCodec.Parse(Encoding.UTF8.GetBytes(root.ToJsonString(new() { WriteIndented = false }))));
    }

    [Fact]
    public void DuplicateNestedOrRootPropertiesAreRejected()
    {
        string canonical = CanonicalJson();
        Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperResultCodec.Parse(
            Encoding.UTF8.GetBytes(canonical.Replace("\"role\":", "\"role\":\"program-files-product\",\"role\":", StringComparison.Ordinal))));
        Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperResultCodec.Parse(
            Encoding.UTF8.GetBytes(canonical.Replace("\"directoryCleanup\":", "\"directoryCleanup\":[],\"directoryCleanup\":", StringComparison.Ordinal))));
    }

    [Fact]
    public void ExistingRepliesKeepTheirCanonicalBytesWithoutAnOptionalReport()
    {
        InstallerMachineHelperCommand command = Command(InstallerOperation.Uninstall);
        InstallerMachineHelperResult result = InstallerMachineHelperResult.Succeeded(command, command.ToDurableState());
        byte[] bytes = InstallerMachineHelperResultCodec.Serialize(result);
        Assert.DoesNotContain("directoryCleanup", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(result, InstallerMachineHelperResultCodec.Parse(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorPublishesCleanupOnlyAfterSuccessfulClearConfirmation(bool failReload)
    {
        var report = new InstallerDirectoryCleanupReport(Entries(InstallerDirectoryCleanupDisposition.RetainedNonEmpty));
        var scenario = new InstallerScenario { DirectoryCleanupReport = report };
        if (failReload)
        {
            scenario.FinalClearResponseAction = _ =>
            {
                scenario.Store.LoadAction = _ => throw new IOException("Cannot observe the cleared state.");
                return Task.CompletedTask;
            };
        }
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(InstallerOperation.Uninstall), null, CancellationToken.None);

        Assert.Equal(failReload ? InstallerExecutionOutcome.Uncertain : InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(failReload ? null : report, result.DirectoryCleanupReport);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public async Task CoordinatorRejectsCleanupReceiptsForOtherOperations(InstallerOperation operation)
    {
        var scenario = new InstallerScenario
        {
            DirectoryCleanupReport = new InstallerDirectoryCleanupReport(Entries()),
            Environment = new(true, "1.0.0.0", false, null),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        InstallerExecutionResult result = await coordinator.ExecuteAsync(InstallerTestData.Request(operation), null, CancellationToken.None);
        Assert.NotEqual(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal("installer.directory_cleanup.result_binding_invalid", result.DiagnosticCode);
        Assert.Null(result.DirectoryCleanupReport);
    }

    private static IEnumerable<InstallerDirectoryCleanupEntry> Entries(
        InstallerDirectoryCleanupDisposition disposition = InstallerDirectoryCleanupDisposition.Deleted) =>
        Enum.GetValues<InstallerDirectoryRole>().Select(role => new InstallerDirectoryCleanupEntry(role, disposition));

    private static InstallerMachineHelperCommand Command(InstallerOperation operation,
        InstallerMachineHelperVerb verb = InstallerMachineHelperVerb.Clear)
    {
        InstallerTransactionJournal journal = InstallerTestData.Journal(operation);
        InstallerTransactionPhase[] phases = operation == InstallerOperation.Uninstall
            ? [InstallerTransactionPhase.MachineRemovalAuthorized, InstallerTransactionPhase.MachineCommitted,
                InstallerTransactionPhase.PackageCommitted, InstallerTransactionPhase.Verified]
            : [InstallerTransactionPhase.MachineReserved, InstallerTransactionPhase.PackageCommitted,
                InstallerTransactionPhase.MachineCommitted, InstallerTransactionPhase.Verified];
        foreach (InstallerTransactionPhase phase in phases)
        {
            journal = journal.TransitionTo(phase);
        }
        InstallerTransactionSnapshot state = InstallerTransactionSnapshot.Create(journal);
        return InstallerMachineHelperCommand.Create(InstallerMachineHelperInvocation.Create(verb, state), state);
    }

    private static string CanonicalJson()
    {
        InstallerMachineHelperCommand command = Command(InstallerOperation.Uninstall);
        return Encoding.UTF8.GetString(InstallerMachineHelperResultCodec.Serialize(
            InstallerMachineHelperResult.Succeeded(command, command.ToDurableState()) with
            {
                DirectoryCleanupReport = new InstallerDirectoryCleanupReport(Entries()),
            }));
    }
}

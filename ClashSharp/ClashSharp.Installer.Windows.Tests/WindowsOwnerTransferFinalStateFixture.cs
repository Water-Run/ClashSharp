using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>In-memory ACL and certificate ports; the payload fixture never creates payloads or edits certificates.</summary>
internal sealed class WindowsOwnerTransferFinalStateFixture : IDisposable
{
    internal WindowsOwnerTransferMachineAccessTests.Fixture Machine { get; } = new();
    internal WindowsOwnerTransferAccessFixture Native => Machine.Native;
    internal InstallerOwnerTransferJournal Journal { get; set; }
    internal WindowsOwnerTransferCertificatePlan Plan => new(Native.Roots, Journal);
    internal CertificateReader Certificates { get; }
    internal WindowsOwnerTransferInstallerAccess Access { get; }
    internal WindowsOwnerTransferCompletion Completion { get; }
    internal string[] InstallerPaths => Journal.NextCertificateLedger is null
        ? [WindowsOwnerTransferAccessFixture.Installer, WindowsOwnerTransferAccessFixture.ContinuationDirectory,
            WindowsOwnerTransferAccessFixture.ContinuationPath]
        : [WindowsOwnerTransferAccessFixture.Installer, WindowsOwnerTransferAccessFixture.ContinuationDirectory,
            WindowsOwnerTransferAccessFixture.ContinuationPath, Plan.ActivePath];

    internal WindowsOwnerTransferFinalStateFixture(bool previous = true, bool next = true)
    {
        InstallerRequest request = WindowsOwnerTransferDeployment.CreateContinuationRequest(Machine.Journal);
        Journal = Machine.Journal with
        {
            Phase = InstallerOwnerTransferPhase.CertificateStateTransferred,
            Generation = (int)InstallerOwnerTransferPhase.CertificateStateTransferred + 1,
            PreviousCertificateLedger = previous
                ? InstallerCertificateOwnershipLedger.Create(request with { TargetSid = WindowsOwnerTransferAccessFixture.PreviousSid },
                    Machine.Release.Release, certificateWasPresent: false) : null,
            NextCertificateLedger = next
                ? InstallerCertificateOwnershipLedger.Create(request, Machine.Release.Release, certificateWasPresent: true) : null,
        };
        foreach ((string path, WindowsOwnerTransferAccessFixture.Entry entry) in Native.Entries)
        {
            if ((path.StartsWith(@"C:\Program Files\ClashSharp", StringComparison.Ordinal)
                    || path.StartsWith(WindowsOwnerTransferAccessFixture.Product, StringComparison.Ordinal))
                && !path.StartsWith(WindowsOwnerTransferAccessFixture.Installer, StringComparison.Ordinal)
                && entry.Security.AccessEntries.Any(ace => ace.Sid == WindowsOwnerTransferAccessFixture.PreviousSid))
            {
                Transfer(path);
            }
        }
        Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes =
            InstallerMachineAssociationCodec.Serialize(Journal.NextOwner.Association);
        Native.Add(Plan.PrivateRoot, directory: true, inherited: false).Security =
            WindowsOwnerTransferAccessFixture.Parse("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        if (Journal.NextCertificateLedger is { } ledger)
        {
            Native.Add(Plan.ActivePath, directory: false, inherited: true).Bytes = InstallerCertificateOwnershipCodec.Serialize(ledger);
        }
        Certificates = new(this);
        Access = new(Machine.Release, Machine.Backend, Native, Certificates);
        Completion = new(Machine.Release, Machine.Backend, Native, Certificates);
    }

    internal void Transfer(string path)
    {
        WindowsOwnerTransferAccessFixture.Entry entry = Native.Entries[path];
        entry.Security = WindowsOwnerTransferAccessFixture.OwnerSecurity(
            WindowsOwnerTransferAccessFixture.NextSid, entry.Directory, entry.Inherited);
    }

    internal void SetCompleted(InstallerOwnerTransferPhase phase = InstallerOwnerTransferPhase.InstallerAccessTransferred)
    {
        foreach (string path in InstallerPaths)
        {
            Transfer(path);
        }
        Journal = Journal with { Phase = phase, Generation = (int)phase + 1 };
    }

    internal Task ApplyAsync(CancellationToken cancellationToken = default) => Access.ApplyAndVerifyAsync(Journal, cancellationToken);
    internal Task VerifyAsync(CancellationToken cancellationToken = default) => Completion.VerifyAsync(Journal, cancellationToken);

    internal void ChangeEvidence(string failure)
    {
        switch (failure)
        {
            case "association":
                Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes =
                    InstallerMachineAssociationCodec.Serialize(Journal.PreviousOwner.Association);
                break;
            case "barrier":
                Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes =
                    InstallerTransactionCodec.Serialize(Journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved));
                break;
            case "active-ledger": Certificates.State = Certificates.State with { Active = Journal.PreviousCertificateLedger }; break;
            case "previous-archive": Certificates.State = Certificates.State with { PreviousArchive = null }; break;
            case "next-archive": Certificates.State = Certificates.State with { NextArchive = Journal.NextCertificateLedger }; break;
            case "private-acl": Native.Entries[Plan.PrivateRoot].Security = WindowsOwnerTransferAccessFixture.Parse("O:BAD:P"); break;
            case "shared-acl":
                Native.Entries[WindowsOwnerTransferAccessFixture.Product].Security =
                    WindowsOwnerTransferAccessFixture.OwnerSecurity(WindowsOwnerTransferAccessFixture.PreviousSid, true, false);
                break;
            case "unknown-child":
                Native.Add(Path.Combine(WindowsOwnerTransferAccessFixture.ContinuationDirectory, "unknown.tmp"), false, true);
                break;
            case "target-certificate-mismatch":
                Journal = Journal with { NextCertificateLedger = Journal.NextCertificateLedger! with { CertificateSha256 = new string('f', 64) } };
                break;
            default: Machine.Failure = failure; break;
        }
    }

    public void Dispose() => Machine.Dispose();

    internal sealed class CertificateReader(WindowsOwnerTransferFinalStateFixture fixture) : IWindowsOwnerTransferCertificateStateReader
    {
        internal InstallerOwnerTransferCertificateState State { get; set; } =
            new(fixture.Journal.NextCertificateLedger, fixture.Journal.PreviousCertificateLedger, null);
        internal int Reads { get; private set; }
        internal Func<int, CancellationToken, Task>? BeforeRead { get; set; }

        public async Task<InstallerOwnerTransferCertificateState> ReadAsync(
            WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken)
        {
            Assert.Equal(fixture.Plan.Roots, plan.Roots);
            Assert.Equal(fixture.Journal.Continuation, plan.Journal.Continuation);
            Assert.True(fixture.Native.LiveLeases > 0);
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (BeforeRead is not null)
            {
                await BeforeRead(Reads, cancellationToken);
            }
            return State;
        }
    }
}

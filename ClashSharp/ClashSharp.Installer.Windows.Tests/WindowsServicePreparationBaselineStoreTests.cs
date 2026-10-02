using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsServicePreparationBaselineStoreTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Token = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task FirstCaptureIsDurableAndExactReplayDoesNotRewriteIt()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        WindowsServicePreparationBaseline baseline = Baseline(fixture);
        var files = new Files();
        var read = new Guard();
        var write = new Guard();
        using var store = new WindowsServicePreparationBaselineStore(fixture.RootDirectory, read, write, () => true, files);

        await store.CaptureOnceAsync(baseline, CancellationToken.None);
        await store.CaptureOnceAsync(baseline, CancellationToken.None);
        WindowsServicePreparationBaseline? observed = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(baseline.Intent, observed?.Intent);
        Assert.Equal(1, files.Writes);
        Assert.Equal(1, write.Calls);
        Assert.True(read.Calls >= 4);
        Assert.Equal(0, files.Deletes);
    }

    [Fact]
    public async Task AnotherTransactionCannotReplaceTheOriginalObservation()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        WindowsServicePreparationBaseline original = Baseline(fixture);
        var files = new Files { Bytes = original.Serialize() };
        byte[] before = files.Bytes.ToArray();
        using var store = Store(fixture.RootDirectory, files);
        WindowsServicePreparationBaseline replacement = new(WindowsServicePreparationBaseline.CurrentSchema,
            original.Intent with { TransactionId = new string('1', 64) }, original.Service);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.CaptureOnceAsync(replacement, CancellationToken.None));

        Assert.Equal("installer.recovery.service_baseline_conflict", failure.DiagnosticCode);
        Assert.Equal(before, files.Bytes);
        Assert.Equal(0, files.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostWriteReceiptIsObservedWithoutRetryingAWrite(bool afterWrite)
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        WindowsServicePreparationBaseline baseline = Baseline(fixture);
        var files = new Files { FailWrite = true, ApplyWrite = afterWrite };
        using var store = Store(fixture.RootDirectory, files);

        Exception? failure = await Record.ExceptionAsync(() => store.CaptureOnceAsync(baseline, CancellationToken.None));

        if (afterWrite) { Assert.Null(failure); Assert.Equal(baseline.Intent, WindowsServicePreparationBaseline.Parse(files.Bytes!).Intent); }
        else { Assert.IsType<InstallerStateUncertainException>(failure); Assert.Null(files.Bytes); }
        Assert.Equal(1, files.Writes);
        Assert.Equal(0, files.Deletes);
    }

    [Fact]
    public async Task CancellationAfterDurableWriteStillReturnsTheObservedOriginalBaseline()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        using var cancellation = new CancellationTokenSource();
        WindowsServicePreparationBaseline baseline = Baseline(fixture);
        var files = new Files { CancelAfterWrite = cancellation };
        using var store = Store(fixture.RootDirectory, files);

        WindowsServicePreparationBaseline saved = await store.CaptureOnceAsync(baseline, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(baseline.Intent, saved.Intent);
        Assert.Equal(1, files.Writes);
    }

    [Fact]
    public async Task MissingPrivateRootReadDoesNotCreateOrTouchAFile()
    {
        var files = new Files();
        var write = new Guard();
        using var store = new WindowsServicePreparationBaselineStore(@"C:\private-test-root", new Guard(), write, () => false, files);

        Assert.Null(await store.ReadAsync(CancellationToken.None));

        Assert.Equal(0, files.Reads);
        Assert.Equal(0, write.Calls);
    }

    private static WindowsServicePreparationBaseline Baseline(WindowsPayloadFixture fixture)
    {
        WindowsMachineDeploymentPlan plan = WindowsMachineDeploymentPlan.Create(fixture.Request(InstallerOperation.Repair, Owner),
            fixture.Manifest, InstallerMachineAssociation.Create(Owner, Token), @"C:\Program Files", @"C:\ProgramData", @"C:\Users\owner");
        return WindowsServicePreparationBaseline.Capture(plan, InstallerTransactionJournal.Create(plan.Request),
            new WindowsServiceSnapshot(plan.Service, WindowsServiceRuntimeState.Running,
                WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(Owner)));
    }

    private static WindowsServicePreparationBaselineStore Store(string root, Files files) => new(root, new Guard(), new Guard(), () => true, files);

    private sealed class Guard : IInstallerTransactionRootGuard
    {
        internal int Calls { get; private set; }
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class Files : IWindowsInstallerPrivateJournalFileNative
    {
        internal byte[]? Bytes { get; set; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal int Deletes { get; private set; }
        internal bool FailWrite { get; init; }
        internal bool ApplyWrite { get; init; } = true;
        internal CancellationTokenSource? CancelAfterWrite { get; init; }

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            Assert.Equal(WindowsServicePreparationBaseline.FileName, Path.GetFileName(path));
            return Task.FromResult(Bytes?.ToArray());
        }

        public Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            if (ApplyWrite) { Bytes = bytes.ToArray(); }
            if (CancelAfterWrite is { } cancel) { cancel.Cancel(); throw new OperationCanceledException(cancellationToken); }
            if (FailWrite) { throw new IOException("lost private baseline receipt"); }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            Deletes++;
            if (Bytes is { } bytes) { CryptographicOperations.ZeroMemory(bytes); }
            Bytes = null;
            return Task.CompletedTask;
        }
    }
}

using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferInstallerAccessTests
{
    [Theory]
    [InlineData(false, false, false, 3)]
    [InlineData(false, true, false, 4)]
    [InlineData(true, false, true, 2)]
    [InlineData(true, true, true, 2)]
    public async Task TransfersOnlyFixedInstallerAccessAndPreservesAllFileBytes(bool previous, bool next, bool propagate, int writes)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture(previous, next);
        fixture.Native.Propagate = propagate;
        var original = fixture.Native.Entries.ToDictionary(pair => pair.Key, pair => (pair.Value.Security, pair.Value.Bytes.ToArray()));
        var certificates = fixture.Certificates.State;
        Assert.Empty(fixture.Native.Opened);
        Assert.Equal(0, fixture.Machine.Release.Calls);

        await fixture.ApplyAsync();

        Assert.Equal(writes, fixture.Native.Writes.Count);
        Assert.Equal(fixture.InstallerPaths.Order(), fixture.Native.WritableOpened.Order());
        foreach ((string path, var entry) in fixture.Native.Entries)
        {
            Assert.Equal(original[path].Item2, entry.Bytes);
            if (!fixture.InstallerPaths.Contains(path))
            {
                Assert.Equal(original[path].Security, entry.Security);
            }
        }
        Assert.Equal(certificates, fixture.Certificates.State);
        Assert.Equal(3, fixture.Certificates.Reads);
        Assert.Equal(2, fixture.Machine.Release.Calls);
        Assert.Equal(3, fixture.Machine.Backend.ServiceChecks);
        Assert.Equal(InstallerOwnerTransferPhase.CertificateStateTransferred, fixture.Journal.Phase);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Machine.Release.Disposed);
        await fixture.ApplyAsync();
        Assert.Equal(writes, fixture.Native.Writes.Count);
    }

    public static TheoryData<int> PartialMasks => new(Enumerable.Range(0, 16));

    [Theory]
    [MemberData(nameof(PartialMasks))]
    public async Task RecoversEveryExactPartialInstallerAclCombination(int mask)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        for (int index = 0; index < fixture.InstallerPaths.Length; index++)
        {
            if ((mask & (1 << index)) != 0)
            {
                fixture.Transfer(fixture.InstallerPaths[index]);
            }
        }

        await fixture.ApplyAsync();

        Assert.Equal(4 - System.Numerics.BitOperations.PopCount((uint)mask), fixture.Native.Writes.Count);
        Assert.All(fixture.InstallerPaths, path => Assert.True(WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
            fixture.Native.Entries[path].Security, WindowsOwnerTransferAccessFixture.NextSid,
            fixture.Native.Entries[path].Directory, fixture.Native.Entries[path].Inherited)));
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("previous-profile-changed")]
    [InlineData("next-profile-changed")]
    [InlineData("different-roots")]
    [InlineData("service-present")]
    [InlineData("service-reappeared-before-write")]
    [InlineData("association")]
    [InlineData("barrier")]
    [InlineData("active-ledger")]
    [InlineData("previous-archive")]
    [InlineData("next-archive")]
    [InlineData("private-acl")]
    [InlineData("shared-acl")]
    [InlineData("unknown-child")]
    [InlineData("target-certificate-mismatch")]
    public async Task FailedPrerequisitesCannotTransferAnyInstallerState(string failure)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.ChangeEvidence(failure);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Empty(fixture.Native.Writes);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("legacy-marker")]
    [InlineData("unknown-directory")]
    [InlineData("missing-barrier")]
    [InlineData("missing-active")]
    [InlineData("unexpected-active")]
    [InlineData("hardlink")]
    [InlineData("reparse")]
    [InlineData("broadened-acl")]
    public async Task RefusesUnknownResidueAndInvalidObjectsBeforeAnyDaclWrite(string failure)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture(next: failure != "unexpected-active");
        switch (failure)
        {
            case "legacy-marker":
                fixture.Native.Add(Path.Combine(WindowsOwnerTransferAccessFixture.Installer, "transaction.json"), false, true);
                break;
            case "unknown-directory":
                fixture.Native.Add(Path.Combine(WindowsOwnerTransferAccessFixture.ContinuationDirectory, "unknown"), true, true);
                break;
            case "missing-barrier": fixture.Native.Entries.Remove(WindowsOwnerTransferAccessFixture.ContinuationPath); break;
            case "missing-active": fixture.Native.Entries.Remove(fixture.Plan.ActivePath); break;
            case "unexpected-active": fixture.Native.Add(fixture.Plan.ActivePath, false, true); break;
            case "hardlink": fixture.Native.Entries[fixture.Plan.ActivePath].Links = 2; break;
            case "reparse": fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationDirectory].Reparse = true; break;
            case "broadened-acl":
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Security =
                    WindowsOwnerTransferAccessFixture.Parse("O:BAD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FR;;;WD)");
                break;
        }

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Empty(fixture.Native.Writes);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("release-postcondition")]
    [InlineData("service-postcondition")]
    [InlineData("previous-profile-changed")]
    [InlineData("next-profile-changed")]
    [InlineData("different-roots")]
    [InlineData("association")]
    [InlineData("barrier")]
    [InlineData("active-ledger")]
    [InlineData("previous-archive")]
    [InlineData("next-archive")]
    [InlineData("private-acl")]
    [InlineData("shared-acl")]
    [InlineData("unknown-child")]
    public async Task ChangedFinalEvidenceCannotAdvanceTheDurablePhase(string failure)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        if (failure == "release-postcondition")
        {
            fixture.Machine.Failure = failure;
        }
        fixture.Machine.Release.PostVerification = _ =>
        {
            fixture.ChangeEvidence(failure);
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Equal(4, fixture.Native.Writes.Count);
        Assert.Equal(InstallerOwnerTransferPhase.CertificateStateTransferred, fixture.Journal.Phase);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailureBetweenDaclWritesCanResumeWithoutRewritingCompletedEntries(int applied)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.Native.BeforeWrite = _ =>
        {
            if (fixture.Native.Writes.Count == applied)
            {
                throw new Win32Exception(5, "Synthetic interrupted DACL transfer.");
            }
        };

        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Equal("installer.owner_transfer.installer_access_failed", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.Equal(applied, fixture.Native.Writes.Count);
        Assert.Equal(0, fixture.Native.LiveLeases);
        fixture.Native.BeforeWrite = null;
        await fixture.ApplyAsync();
        Assert.Equal(4, fixture.Native.Writes.Count);
        Assert.Equal(4, fixture.Native.Writes.Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDrainsOwnedObservationBeforeReleasingPinnedFiles(bool duringRelease)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Hold()
        {
            entered.TrySetResult();
            return drain.Task;
        }
        if (duringRelease)
        {
            fixture.Machine.Release.PostVerification = _ => Hold();
        }
        else
        {
            fixture.Certificates.BeforeRead = (count, _) => count == 2 ? Hold() : Task.CompletedTask;
        }
        Task pending = fixture.ApplyAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.Equal(fixture.Native.Opened.Count, fixture.Native.LiveLeases);
            Assert.Equal(4, fixture.Native.Writes.Count);
        }
        finally
        {
            cancellation.Cancel();
            drain.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Machine.Release.Disposed);
    }
}

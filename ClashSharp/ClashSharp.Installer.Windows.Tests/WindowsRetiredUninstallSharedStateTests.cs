using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Retirement;
using ClashSharp.Installer.Windows.Transactions;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsRetiredUninstallSharedStateTests
{
    private const string OldSid = WindowsOwnerTransferAccessFixture.PreviousSid;
    private const string NewSid = WindowsOwnerTransferAccessFixture.NextSid;
    private const string Product = @"C:\ProgramData\ClashSharp";
    private const string Data = Product + @"\MihomoService";
    private const string Installer = Product + @"\Installer";
    private const string JournalRoot = Installer + @"\v2";

    [Fact]
    public async Task DifferentOwnerIsObservedWithoutServiceInspectionOrNewOwnerApplicationLocks()
    {
        var native = new Native();
        using var state = Create(native);
        Assert.Equal(0, native.Opened);
        await state.ReverifyAsync(default);
        int opened = native.Opened;
        await state.ReverifyAsync(default);
        Assert.Equal(opened, native.Opened);
        Assert.Equal(0, native.ServiceAbsenceChecks);
        Assert.All(native.Profiles, sid => Assert.Equal(OldSid, sid));
        state.Dispose();
        Assert.Equal(0, native.Live);
        Assert.All(native.Buffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyUninstalledSharedServiceAllowsCleanupWithRemainingOrAbsentRoots(bool removeRoots)
    {
        var native = new Native { Association = null };
        if (removeRoots)
        {
            foreach (string path in native.Directories.Keys.Where(path => path.Contains(@"\ClashSharp\", StringComparison.Ordinal)).ToArray())
            {
                native.Directories.Remove(path);
            }
        }
        using var state = Create(native);
        await state.ReverifyAsync(default);
        await state.ReverifyAsync(default);
        Assert.Equal(2, native.ServiceAbsenceChecks);
        state.Dispose();
        Assert.Equal(0, native.Live);
    }

    [Fact]
    public async Task CurrentOwnerCannotUseRetiredCopyRemoval()
    {
        var native = new Native(OldSid);
        using var state = Create(native);
        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => state.ReverifyAsync(default));
        Assert.Equal("installer.retired_uninstall.current_owner", error.DiagnosticCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryPendingEvidenceBlocksEvenWithoutASharedAssociation(bool associationPresent)
    {
        var native = new Native { OrdinaryPresent = true };
        if (!associationPresent) { native.Association = null; }
        using var state = Create(native);
        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => state.ReverifyAsync(default));
        Assert.Equal("installer.retired_uninstall.ordinary_state_pending", error.DiagnosticCode);
    }

    [Theory]
    [InlineData("association")]
    [InlineData("owner")]
    [InlineData("profile")]
    [InlineData("ordinary")]
    [InlineData("reparse")]
    [InlineData("file")]
    [InlineData("acl")]
    public async Task ChangedEvidenceBetweenCheckpointsCannotAuthorizeFurtherRemoval(string fault)
    {
        var native = new Native();
        using var state = Create(native);
        await state.ReverifyAsync(default);
        switch (fault)
        {
            case "association": native.Association = InstallerMachineAssociationCodec.Serialize(InstallerMachineAssociation.Create(NewSid, new string('f', 64))); break;
            case "owner": native.Directories[Installer] = Observation(OldSid); break;
            case "profile": native.Profile = @"C:\Users\Changed"; break;
            case "ordinary": native.OrdinaryPresent = true; break;
            case "reparse": native.Directories[Data] = native.Directories[Data] with { IsReparsePoint = true }; break;
            case "file": native.Directories[Data] = native.Directories[Data] with { IsDirectory = false }; break;
            case "acl": native.Directories[Data] = native.Directories[Data] with { Security = native.Directories[Data].Security with { DaclProtected = false } }; break;
        }
        await Assert.ThrowsAsync<InstallerProtocolException>(() => state.ReverifyAsync(default));
        state.Dispose();
        Assert.Equal(0, native.Live);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("space")]
    [InlineData("malformed")]
    [InlineData("unsafe")]
    [InlineData("missing-root")]
    [InlineData("mixed-owner")]
    [InlineData("ambiguous-owner")]
    public async Task InconsistentInitialSharedStateCannotBeAccepted(string fault)
    {
        var native = new Native();
        switch (fault)
        {
            case "service": native.Association = null; native.ServicePresent = true; break;
            case "space": native.Association = [32, .. native.Association!]; break;
            case "malformed": native.Association = Encoding.UTF8.GetBytes("{\"private\":\"fixture\"}"); break;
            case "unsafe": native.UnsafeAssociation = true; break;
            case "missing-root": native.Directories.Remove(JournalRoot); break;
            case "mixed-owner": native.Directories[Installer] = Observation(OldSid); break;
            case "ambiguous-owner":
                native.Directories[Installer] = Observation(NewSid) with { Security = Observation(NewSid).Security with { AccessEntries = [] } }; break;
        }
        using var state = Create(native);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => state.ReverifyAsync(default));
        state.Dispose();
        Assert.Equal(0, native.Live);
    }

    [Fact]
    public async Task PreviouslyAbsentDirectoryCannotAppearDuringRemoval()
    {
        var native = new Native { Association = null };
        native.Directories.Remove(JournalRoot);
        using var state = Create(native);
        await state.ReverifyAsync(default);
        native.Directories[JournalRoot] = Observation(NewSid);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => state.ReverifyAsync(default));
    }

    [Fact]
    public async Task CancellationBeforeInspectionOpensNothingAndDisposalRejectsReuse()
    {
        var native = new Native();
        var state = Create(native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.ReverifyAsync(new CancellationToken(true)));
        Assert.Equal(0, native.Opened);
        state.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => state.ReverifyAsync(default));
    }

    private static WindowsRetiredUninstallSharedState Create(Native native) => new(OldSid, @"C:\ProgramData", @"C:\Program Files", native);
    private static WindowsDirectoryObservation Observation(string sid) =>
        new(true, false, WindowsOwnerTransferAccessFixture.OwnerSecurity(sid, directory: true, inherited: false));

    private sealed class Native : IWindowsRetiredUninstallObservationNative
    {
        internal Dictionary<string, WindowsDirectoryObservation> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal byte[]? Association;
        internal bool OrdinaryPresent;
        internal bool ServicePresent;
        internal bool UnsafeAssociation;
        internal int ServiceAbsenceChecks;
        internal int Opened;
        internal int Live;
        internal string Profile = @"C:\Users\Previous";
        internal List<string> Profiles { get; } = [];
        internal List<byte[]> Buffers { get; } = [];
        internal Native(string owner = NewSid)
        {
            foreach (string path in new[] { @"C:\", @"C:\ProgramData", @"C:\Program Files",
                Product, Installer, JournalRoot, Data, @"C:\Program Files\ClashSharp", @"C:\Program Files\ClashSharp\Service" })
            {
                Directories[path] = Observation(owner);
            }
            Association = InstallerMachineAssociationCodec.Serialize(InstallerMachineAssociation.Create(owner, new string('a', 64)));
        }
        public IWindowsInstallerDirectoryLease OpenDirectory(string path)
        {
            Opened++;
            if (!Directories.ContainsKey(path)) { throw new DirectoryNotFoundException(); }
            Live++;
            return new Lease(this, path);
        }
        public WindowsMachineAssociationFileObservation ReadAssociation(string path, string ownerSid)
        {
            Assert.Equal(Path.Combine(Data, "association.json"), path);
            Assert.True(Live > 0);
            if (UnsafeAssociation) { return new(WindowsMachineAssociationFileStatus.Unsafe, null); }
            if (Association is null) { return new(WindowsMachineAssociationFileStatus.Missing, null); }
            byte[] bytes = Association.ToArray();
            Buffers.Add(bytes);
            return new(WindowsMachineAssociationFileStatus.OrdinaryFile, bytes);
        }
        public bool IsEntryPresent(string path) => Directories.ContainsKey(path)
            || path == Path.Combine(JournalRoot, InstallerStateLayout.JournalFileName) && OrdinaryPresent;
        public string ResolveProfile(string targetSid, CancellationToken cancellationToken) { Profiles.Add(targetSid); return Profile; }
        public void VerifyServiceAbsent(CancellationToken cancellationToken)
        {
            ServiceAbsenceChecks++;
            if (ServicePresent) { throw new InstallerProtocolException("installer.machine.service_present"); }
        }
        private sealed class Lease(Native native, string path) : IWindowsInstallerDirectoryLease
        {
            public WindowsDirectoryObservation Observe() => native.Directories[path];
            public void Dispose() => native.Live--;
        }
    }
}

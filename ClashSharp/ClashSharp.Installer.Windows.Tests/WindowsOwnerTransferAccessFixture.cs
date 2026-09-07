using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Tests;

internal sealed class WindowsOwnerTransferAccessFixture : IWindowsOwnerTransferAccessNative
{
    internal const string PreviousSid = "S-1-5-21-100-200-300-1001";
    internal const string NextSid = "S-1-5-21-100-200-300-1002";
    internal const string Product = @"C:\ProgramData\ClashSharp";
    internal const string Machine = @"C:\Program Files\ClashSharp\Service";
    internal const string ServiceData = Product + @"\MihomoService";
    internal const string AssociationPath = ServiceData + @"\association.json";
    internal const string Installer = Product + @"\Installer";
    internal const string Authority = Product + @"\InstallerAuthority";
    internal const string ContinuationDirectory = Installer + @"\v2";
    internal static readonly string ContinuationPath = Path.Combine(ContinuationDirectory, InstallerStateLayout.JournalFileName);
    internal const string Payload = Machine + @"\current\Host\ClashSharp.MihomoService.exe";
    internal static readonly InstallerMachineAssociation Association =
        InstallerMachineAssociation.Create(PreviousSid, new string('b', 64));
    internal static readonly string PrivateService = Path.Combine(ServiceData, Association.BuildServicePipeName());
    internal Dictionary<string, Entry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<string> Opened { get; } = [];
    internal List<string> Disposed { get; } = [];
    internal List<string> Writes { get; } = [];
    internal List<string> Enumerated { get; } = [];
    internal bool Propagate { get; set; }
    internal Action<string>? BeforeWrite { get; set; }
    internal Action<string>? BeforeEnumerate { get; set; }
    internal Action<string>? BeforeObserve { get; set; }
    internal Action<string>? BeforeRead { get; set; }
    internal int LiveLeases { get; private set; }
    internal WindowsMachineDeploymentRoots Roots { get; } = WindowsMachineDeploymentRoots.Create(
        @"C:\Program Files", @"C:\ProgramData");

    internal WindowsOwnerTransferAccessFixture()
    {
        foreach (string path in new[] { @"C:\", @"C:\Program Files", @"C:\ProgramData",
            @"C:\Program Files\ClashSharp", Machine, Product, ServiceData, Installer })
        {
            Add(path, directory: true, inherited: false);
        }
        Add(Machine + @"\current", directory: true, inherited: true);
        Add(Machine + @"\current\Host", directory: true, inherited: true);
        Add(Payload, directory: false, inherited: true);
        Add(AssociationPath, directory: false, inherited: true).Bytes = InstallerMachineAssociationCodec.Serialize(Association);
        Add(Authority, directory: true, inherited: false).Security =
            Parse("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        Add(PrivateService, directory: true, inherited: false).Security =
            Parse("O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        // Opaque private descendants deliberately have unsupported ACLs. The machine-access
        // phase must not enumerate, read or repair them across the protected boundary.
        Add(Authority + @"\owner-transfer-private.json", directory: false, inherited: false).Security = Parse("O:BAD:P");
        Add(PrivateService + @"\private-runtime.json", directory: false, inherited: false).Security = Parse("O:SYD:P");
        Add(ContinuationDirectory, directory: true, inherited: false);
        Add(ContinuationPath, directory: false, inherited: true);
    }

    internal Entry Add(string path, bool directory, bool inherited)
    {
        var entry = new Entry(directory, inherited, OwnerSecurity(PreviousSid, directory, inherited));
        Entries.Add(path, entry);
        return entry;
    }

    public IWindowsOwnerTransferAccessLease Open(string path, bool directory, bool canChangeAccess)
    {
        Opened.Add(path);
        if (!Entries.TryGetValue(path, out Entry? entry))
        {
            throw new FileNotFoundException("Synthetic tree member missing.");
        }
        LiveLeases++;
        return new Lease(this, path, entry, directory, canChangeAccess);
    }

    public IEnumerable<WindowsOwnerTransferAccessEntry> EnumerateChildren(string path)
    {
        Enumerated.Add(path);
        BeforeEnumerate?.Invoke(path);
        return Entries.Where(entry => string.Equals(
                Path.GetDirectoryName(entry.Key), path, StringComparison.OrdinalIgnoreCase))
            .Select(static entry => new WindowsOwnerTransferAccessEntry(entry.Key, entry.Value.Directory)).ToArray();
    }

    internal WindowsOwnerTransferAccessTree Acquire(CancellationToken cancellationToken = default) =>
        WindowsOwnerTransferAccessTree.Acquire(Roots, this, PreviousSid, NextSid, cancellationToken);

    internal static WindowsDirectorySecuritySnapshot OwnerSecurity(string sid, bool directory, bool inherited)
    {
        string flags = directory ? (inherited ? "OICIID" : "OICI") : (inherited ? "ID" : string.Empty);
        return Parse($"O:BAD:{(inherited ? string.Empty : "P")}(A;{flags};FA;;;SY)(A;{flags};FA;;;BA)(A;{flags};0x1200a9;;;{sid})");
    }

    internal static WindowsDirectorySecuritySnapshot Parse(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        return new WindowsDirectorySecuritySnapshot(descriptor.Owner?.Value,
            descriptor.DiscretionaryAcl is not null,
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
            descriptor.DiscretionaryAcl?.Cast<CommonAce>().Select(ace => new WindowsDirectoryAce(
                ace.SecurityIdentifier.Value,
                ace.AceQualifier == AceQualifier.AccessAllowed ? WindowsDirectoryAceKind.Allow : WindowsDirectoryAceKind.Deny,
                ace.AccessMask, ace.AceFlags, false)).ToArray() ?? []);
    }

    internal sealed class Entry(bool directory, bool inherited, WindowsDirectorySecuritySnapshot security)
    {
        internal bool Directory { get; } = directory;
        internal bool Inherited { get; } = inherited;
        internal WindowsDirectorySecuritySnapshot Security { get; set; } = security;
        internal bool Reparse { get; set; }
        internal uint Links { get; set; } = 1;
        internal byte[] Bytes { get; set; } = [1, 2, 3];
    }

    private sealed class Lease(WindowsOwnerTransferAccessFixture owner, string path, Entry entry,
        bool directory, bool canChangeAccess) : IWindowsOwnerTransferAccessLease
    {
        private bool _disposed;

        public WindowsOwnerTransferAccessObservation Observe()
        {
            Assert.False(_disposed);
            owner.BeforeObserve?.Invoke(path);
            return new(entry.Directory, entry.Reparse, entry.Links, entry.Security);
        }

        public byte[] ReadFileBytes(int maximumBytes)
        {
            Assert.False(_disposed);
            Assert.False(directory);
            owner.BeforeRead?.Invoke(path);
            Assert.True(entry.Bytes.Length <= maximumBytes);
            return entry.Bytes.ToArray();
        }

        public void ApplyOwnerAccess(string previousSid, string nextSid, bool inherited)
        {
            Assert.False(_disposed);
            Assert.True(canChangeAccess);
            Assert.Equal(PreviousSid, previousSid);
            Assert.Equal(NextSid, nextSid);
            Assert.Equal(entry.Inherited, inherited);
            if (entry.Security.AccessEntries.Any(ace => ace.Sid == nextSid))
            {
                return;
            }
            owner.BeforeWrite?.Invoke(path);
            owner.Writes.Add(path);
            entry.Security = OwnerSecurity(nextSid, directory, inherited);
            if (owner.Propagate && directory)
            {
                PropagateToChildren(path, nextSid);
            }
        }

        private void PropagateToChildren(string parent, string nextSid)
        {
            foreach ((string childPath, Entry child) in owner.Entries.Where(pair =>
                         string.Equals(Path.GetDirectoryName(pair.Key), parent, StringComparison.OrdinalIgnoreCase)))
            {
                if (!child.Inherited)
                {
                    continue;
                }
                child.Security = OwnerSecurity(nextSid, child.Directory, inherited: true);
                if (child.Directory)
                {
                    PropagateToChildren(childPath, nextSid);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            owner.LiveLeases--;
            owner.Disposed.Add(path);
            _disposed = true;
        }
    }
}

using System.Buffers;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Pins both product trees before any DACL write. Only exact old/new shared ACLs are transferable.
/// Protected Installer and service-private directories terminate enumeration and retain their ACLs.
/// Parents precede descendants so partial inheritance propagation can be completed on replay.
/// </summary>
internal sealed class WindowsOwnerTransferAccessTree : IDisposable
{
    private const int MaximumEntries = 256;
    private const int MaximumDepth = 12;
    private const string ServiceDirectoryPrefix = "ClashSharp.Mihomo.";
    private static readonly SearchValues<char> LowerHexCharacters = SearchValues.Create("0123456789abcdef");
    private readonly IWindowsOwnerTransferAccessNative _native;
    private readonly string _previousSid;
    private readonly string _nextSid;
    private readonly List<Node> _nodes = [];
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private WindowsOwnerTransferAccessTree(
        IWindowsOwnerTransferAccessNative native, string previousSid, string nextSid)
    {
        ArgumentNullException.ThrowIfNull(native);
        WindowsOwnerTransferAccessPolicy.ValidateParticipants(previousSid, nextSid);
        _native = native;
        _previousSid = previousSid;
        _nextSid = nextSid;
    }

    internal static WindowsOwnerTransferAccessTree Acquire(
        WindowsMachineDeploymentRoots roots, IWindowsOwnerTransferAccessNative native,
        string previousSid, string nextSid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        roots.Validate();
        var tree = new WindowsOwnerTransferAccessTree(native, previousSid, nextSid);
        try
        {
            tree.AcquireAnchors(roots.ProgramFilesRoot, cancellationToken);
            tree.AcquireAnchors(roots.CommonApplicationDataRoot, cancellationToken);
            tree.AcquireNode(new Spec(Path.Combine(roots.ProgramFilesRoot, "ClashSharp"), Role.ProgramFilesProduct), 0, cancellationToken);
            tree.AcquireNode(new Spec(Path.Combine(roots.CommonApplicationDataRoot, "ClashSharp"), Role.ProgramDataProduct), 0, cancellationToken);
            tree.Reverify(requireTransferred: false, cancellationToken);
            return tree;
        }
        catch
        {
            tree.Dispose();
            throw;
        }
    }

    internal void VerifyAssociation(InstallerMachineAssociation expected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(expected);
        Node node = _nodes.Single(entry => entry.Spec.Role == Role.Association);
        byte[] bytes = node.Lease.ReadFileBytes(InstallerMachineAssociationCodec.MaximumAssociationBytes);
        try
        {
            if (InstallerMachineAssociationCodec.Parse(bytes) != expected)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_association_changed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal void VerifyContinuation(InstallerTransactionJournal expected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(expected);
        Node node = _nodes.Single(entry => entry.Spec.Role == Role.ContinuationFile);
        byte[] bytes = node.Lease.ReadFileBytes(InstallerTransactionCodec.MaximumDocumentBytes);
        try
        {
            if (InstallerTransactionCodec.Parse(bytes) != expected)
            {
                throw new InstallerProtocolException("installer.owner_transfer.continuation_changed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal void ApplyAndVerify(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Reverify(requireTransferred: false, cancellationToken);
        foreach (Node node in _nodes.Where(static node => node.Spec.CanChange))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(node, requireTransferred: false);
            node.Lease.ApplyOwnerAccess(_previousSid, _nextSid, node.Spec.Inherited);
        }
        Reverify(requireTransferred: true, cancellationToken);
    }

    internal void Reverify(bool requireTransferred, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (Node node in _nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(node, requireTransferred);
            if (node.Children is null)
            {
                continue;
            }
            Dictionary<string, bool> actual = ReadChildren(node.Spec.Path, cancellationToken);
            if (actual.Count != node.Children.Count
                || actual.Any(entry => !node.Children.TryGetValue(entry.Key, out bool directory) || directory != entry.Value))
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_tree_changed");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        for (int index = _nodes.Count - 1; index >= 0; index--)
        {
            _nodes[index].Lease.Dispose();
        }
        _nodes.Clear();
        _disposed = true;
    }

    private void AcquireAnchors(string root, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        string? path = root;
        while (path is not null)
        {
            if (paths.Count >= 64)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_tree_limit");
            }
            paths.Add(path);
            path = Path.GetDirectoryName(path);
        }
        for (int index = paths.Count - 1; index >= 0; index--)
        {
            if (_paths.Contains(paths[index]))
            {
                continue;
            }
            AcquireNode(new Spec(paths[index], Role.Anchor), 0, cancellationToken);
        }
    }

    private void AcquireNode(Spec spec, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaximumDepth || _nodes.Count >= MaximumEntries || !_paths.Add(spec.Path))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_tree_limit");
        }
        IWindowsOwnerTransferAccessLease lease = _native.Open(spec.Path, spec.Directory, spec.CanChange);
        WindowsDirectorySecuritySnapshot original;
        try
        {
            original = lease.Observe().Security;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        var node = new Node(spec, lease, original);
        // After the first guarded observation, the tree owns every remaining failure path.
        _nodes.Add(node);
        Validate(node, requireTransferred: false);
        // The protected Installer subtree is opaque except for this fixed, read-only barrier.
        // A normal reader requires the old product-root ACL and cannot span its transfer.
        if (spec.Role is Role.InstallerBoundary or Role.ContinuationDirectory)
        {
            bool installer = spec.Role == Role.InstallerBoundary;
            AcquireNode(new Spec(Path.Combine(spec.Path, installer
                ? InstallerStateLayout.VersionDirectoryName : InstallerStateLayout.JournalFileName),
                installer ? Role.ContinuationDirectory : Role.ContinuationFile), checked(depth + 1), cancellationToken);
        }
        if (!spec.Enumerate)
        {
            return;
        }

        node.Children = ReadChildren(spec.Path, cancellationToken);
        ValidateFixedChildren(spec, node.Children);
        foreach ((string childPath, bool directory) in node.Children.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            Spec child = DescribeChild(spec, childPath, directory);
            AcquireNode(child, checked(depth + 1), cancellationToken);
        }
    }

    private Dictionary<string, bool> ReadChildren(string path, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (WindowsOwnerTransferAccessEntry entry in _native.EnumerateChildren(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateChildPath(path, entry.Path);
            if (entries.Count >= MaximumEntries || !entries.TryAdd(entry.Path, entry.IsDirectory))
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_tree_limit");
            }
        }
        return entries;
    }

    private void Validate(Node node, bool requireTransferred)
    {
        WindowsOwnerTransferAccessObservation observation = node.Lease.Observe();
        if (observation.IsDirectory != node.Spec.Directory || observation.IsReparsePoint
            || (!observation.IsDirectory && observation.LinkCount != 1))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_object_invalid");
        }
        WindowsDirectorySecuritySnapshot security = observation.Security;
        bool accepted = node.Spec.Role switch
        {
            Role.Anchor => WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(security),
            Role.InstallerBoundary or Role.ContinuationDirectory or Role.ContinuationFile => WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
                security, _previousSid, node.Spec.Directory, node.Spec.Inherited),
            Role.AuthorityBoundary => WindowsOwnerTransferAccessPolicy.HasPrivateDirectoryAccess(security, service: false),
            Role.ServiceBoundary => WindowsOwnerTransferAccessPolicy.HasPrivateDirectoryAccess(security, service: true),
            _ => WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
                    security, _nextSid, node.Spec.Directory, node.Spec.Inherited)
                || (!requireTransferred && WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
                    security, _previousSid, node.Spec.Directory, node.Spec.Inherited)),
        };
        if (!accepted || (node.Spec.PreservedBoundary && !SameSecurity(node.OriginalSecurity, security)))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_acl_invalid");
        }
    }

    private static bool SameSecurity(WindowsDirectorySecuritySnapshot left, WindowsDirectorySecuritySnapshot right) =>
        left.OwnerSid == right.OwnerSid && left.HasDacl == right.HasDacl
        && left.DaclProtected == right.DaclProtected && left.AccessEntries.SequenceEqual(right.AccessEntries);

    private static void ValidateChildPath(string parent, string path)
    {
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Any(char.IsControl) || IsDeviceName(name)
            || !string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_path_invalid");
        }
    }

    private static bool IsDeviceName(string name)
    {
        string stem = name.Split('.')[0].TrimEnd(' ');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    private static void ValidateFixedChildren(Spec parent, IReadOnlyDictionary<string, bool> children)
    {
        string[] required = parent.Role switch
        {
            Role.ProgramFilesProduct => ["Service"],
            Role.ProgramDataProduct => ["MihomoService", "Installer", "InstallerAuthority"],
            Role.ServiceDataRoot => ["association.json"],
            _ => [],
        };
        foreach (string name in required)
        {
            if (!children.ContainsKey(Path.Combine(parent.Path, name)))
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_required_state_missing");
            }
        }
    }

    private static Spec DescribeChild(Spec parent, string path, bool directory)
    {
        string name = Path.GetFileName(path);
        Role? role = parent.Role switch
        {
            Role.ProgramFilesProduct when directory && name.Equals("Service", StringComparison.OrdinalIgnoreCase) => Role.MachineRoot,
            Role.ProgramDataProduct when directory && name.Equals("MihomoService", StringComparison.OrdinalIgnoreCase) => Role.ServiceDataRoot,
            Role.ProgramDataProduct when directory && name.Equals("Installer", StringComparison.OrdinalIgnoreCase) => Role.InstallerBoundary,
            Role.ProgramDataProduct when directory && name.Equals("InstallerAuthority", StringComparison.OrdinalIgnoreCase) => Role.AuthorityBoundary,
            Role.MachineRoot when directory && (name.Equals("current", StringComparison.OrdinalIgnoreCase)
                || name.Equals("staging", StringComparison.OrdinalIgnoreCase) || name.Equals("previous", StringComparison.OrdinalIgnoreCase)) => Role.PayloadDirectory,
            Role.PayloadDirectory => directory ? Role.PayloadDirectory : Role.PayloadFile,
            Role.ServiceDataRoot when !directory && name.Equals("association.json", StringComparison.OrdinalIgnoreCase) => Role.Association,
            Role.ServiceDataRoot when directory && IsServiceDirectoryName(name) => Role.ServiceBoundary,
            _ => null,
        };
        return role is { } value ? new Spec(path, value)
            : throw new InstallerProtocolException("installer.owner_transfer.access_entry_invalid");
    }

    private static bool IsServiceDirectoryName(string name) =>
        name.StartsWith(ServiceDirectoryPrefix, StringComparison.Ordinal)
        && name.Length == ServiceDirectoryPrefix.Length + 32
        && name.AsSpan(ServiceDirectoryPrefix.Length).IndexOfAnyExcept(LowerHexCharacters) < 0;

    private enum Role
    {
        Anchor, ProgramFilesProduct, ProgramDataProduct, MachineRoot, ServiceDataRoot,
        PayloadDirectory, PayloadFile, Association, InstallerBoundary, AuthorityBoundary, ServiceBoundary,
        ContinuationDirectory, ContinuationFile,
    }

    private sealed record Spec(string Path, Role Role)
    {
        internal bool Directory => Role is not (Role.PayloadFile or Role.Association or Role.ContinuationFile);
        internal bool PreservedBoundary => Role is Role.InstallerBoundary or Role.AuthorityBoundary or Role.ServiceBoundary
            or Role.ContinuationDirectory or Role.ContinuationFile;
        internal bool CanChange => Role != Role.Anchor && !PreservedBoundary;
        internal bool Inherited => Role is Role.PayloadDirectory or Role.PayloadFile or Role.Association or Role.ContinuationFile;
        internal bool Enumerate => Directory && CanChange;
    }

    private sealed class Node(Spec spec, IWindowsOwnerTransferAccessLease lease, WindowsDirectorySecuritySnapshot originalSecurity)
    {
        internal Spec Spec { get; } = spec;
        internal IWindowsOwnerTransferAccessLease Lease { get; } = lease;
        internal WindowsDirectorySecuritySnapshot OriginalSecurity { get; } = originalSecurity;
        internal Dictionary<string, bool>? Children { get; set; }
    }
}

using System.Buffers;
using System.Security.Cryptography;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Pins both product trees before any DACL write. Only exact old/new shared ACLs are transferable.
/// The final Installer phase enumerates only its fixed state leaves. Other phases keep that subtree
/// opaque; service-private directories always terminate enumeration and retain their ACLs.
/// Parents precede descendants so partial inheritance propagation can be completed on replay.
/// </summary>
internal sealed class WindowsOwnerTransferAccessTree : IDisposable, IWindowsOwnerTransferCertificateBoundary
{
    private const int MaximumEntries = 256;
    private const int MaximumDepth = 12;
    private const string ServiceDirectoryPrefix = "ClashSharp.Mihomo.";
    private static readonly SearchValues<char> LowerHexCharacters = SearchValues.Create("0123456789abcdef");
    private readonly IWindowsOwnerTransferAccessNative _native;
    private readonly string _previousSid;
    private readonly string _nextSid;
    private readonly string? _associationTemporaryPath;
    private readonly Purpose _purpose;
    private readonly bool _activeCertificatePresent;
    private readonly string? _startupContinuationPath;
    private readonly List<Node> _nodes = [];
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private WindowsOwnerTransferAccessTree(
        IWindowsOwnerTransferAccessNative native, string previousSid, string nextSid,
        Purpose purpose = Purpose.MachineAccess, string? associationTemporaryPath = null,
        bool activeCertificatePresent = false, string? startupContinuationPath = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        WindowsOwnerTransferAccessPolicy.ValidateParticipants(previousSid, nextSid);
        _native = native;
        _previousSid = previousSid;
        _nextSid = nextSid;
        _associationTemporaryPath = associationTemporaryPath;
        _purpose = purpose;
        _activeCertificatePresent = activeCertificatePresent;
        _startupContinuationPath = startupContinuationPath;
    }

    internal static WindowsOwnerTransferAccessTree Acquire(
        WindowsMachineDeploymentRoots roots, IWindowsOwnerTransferAccessNative native,
        string previousSid, string nextSid, CancellationToken cancellationToken)
    {
        var tree = new WindowsOwnerTransferAccessTree(native, previousSid, nextSid);
        return AcquireCore(roots, tree, cancellationToken);
    }

    internal static WindowsOwnerTransferAccessTree AcquireForStartupBlock(
        WindowsMachineDeploymentRoots roots, InstallerOwnerTransferJournal journal,
        IWindowsOwnerTransferAccessNative native, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        if (journal.Phase != InstallerOwnerTransferPhase.Prepared)
        {
            throw new InstallerProtocolException("installer.owner_transfer.startup_phase_invalid");
        }
        var tree = new WindowsOwnerTransferAccessTree(native, journal.PreviousOwner.Association.OwnerSid,
            journal.NextOwner.Association.OwnerSid, Purpose.StartupBlock,
            activeCertificatePresent: journal.PreviousCertificateLedger is not null,
            startupContinuationPath: Path.Combine(roots.CommonApplicationDataRoot,
                InstallerStateLayout.ProductDirectoryName, InstallerStateLayout.InstallerDirectoryName,
                InstallerStateLayout.VersionDirectoryName, InstallerStateLayout.JournalFileName));
        return AcquireCore(roots, tree, cancellationToken);
    }

    internal static IWindowsOwnerTransferAssociationBoundary AcquireForAssociationTransfer(
        WindowsOwnerTransferAssociationPlan plan, IWindowsOwnerTransferAccessNative native, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        var tree = new WindowsOwnerTransferAccessTree(native, plan.Previous.OwnerSid, plan.Next.OwnerSid,
            Purpose.Association, plan.TemporaryPath);
        return AcquireCore(plan.Roots, tree, cancellationToken);
    }

    internal static IWindowsOwnerTransferCertificateBoundary AcquireForCertificateTransfer(
        WindowsOwnerTransferCertificatePlan plan, IWindowsOwnerTransferAccessNative native, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        if (plan.Journal.Phase != InstallerOwnerTransferPhase.AssociationTransferred)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_phase_invalid");
        }
        var tree = new WindowsOwnerTransferAccessTree(native, plan.Journal.PreviousOwner.Association.OwnerSid,
            plan.Journal.NextOwner.Association.OwnerSid, Purpose.Certificates);
        return AcquireCore(plan.Roots, tree, cancellationToken);
    }

    internal static WindowsOwnerTransferAccessTree AcquireForInstallerAccess(
        WindowsOwnerTransferCertificatePlan plan, IWindowsOwnerTransferAccessNative native, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        if (plan.Journal.Phase != InstallerOwnerTransferPhase.CertificateStateTransferred)
        {
            throw new InstallerProtocolException("installer.owner_transfer.installer_access_phase_invalid");
        }
        return AcquireFinalStateTree(plan, native, Purpose.InstallerAccess, cancellationToken);
    }

    internal static IWindowsOwnerTransferCertificateBoundary AcquireForCompletedTransfer(
        WindowsOwnerTransferCertificatePlan plan, IWindowsOwnerTransferAccessNative native, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        if (plan.Journal.Phase is not (InstallerOwnerTransferPhase.InstallerAccessTransferred or InstallerOwnerTransferPhase.Verified))
        {
            throw new InstallerProtocolException("installer.owner_transfer.completion_phase_invalid");
        }
        return AcquireFinalStateTree(plan, native, Purpose.Completed, cancellationToken);
    }

    private static WindowsOwnerTransferAccessTree AcquireFinalStateTree(WindowsOwnerTransferCertificatePlan plan,
        IWindowsOwnerTransferAccessNative native, Purpose purpose, CancellationToken cancellationToken)
    {
        var tree = new WindowsOwnerTransferAccessTree(native, plan.Journal.PreviousOwner.Association.OwnerSid,
            plan.Journal.NextOwner.Association.OwnerSid, purpose,
            activeCertificatePresent: plan.Journal.NextCertificateLedger is not null);
        return AcquireCore(plan.Roots, tree, cancellationToken);
    }

    private static WindowsOwnerTransferAccessTree AcquireCore(
        WindowsMachineDeploymentRoots roots, WindowsOwnerTransferAccessTree tree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        roots.Validate();
        try
        {
            tree.AcquireAnchors(roots.ProgramFilesRoot, cancellationToken);
            tree.AcquireAnchors(roots.CommonApplicationDataRoot, cancellationToken);
            tree.AcquireNode(new Spec(Path.Combine(roots.ProgramFilesRoot, "ClashSharp"), Role.ProgramFilesProduct), 0, cancellationToken);
            tree.AcquireNode(new Spec(Path.Combine(roots.CommonApplicationDataRoot, "ClashSharp"), Role.ProgramDataProduct), 0, cancellationToken);
            tree.Reverify(requireTransferred: tree.RequireTransferredOnAcquisition, cancellationToken);
            return tree;
        }
        catch
        {
            tree.Dispose();
            throw;
        }
    }

    void IWindowsOwnerTransferAssociationBoundary.Reverify(CancellationToken cancellationToken) =>
        Reverify(requireTransferred: true, cancellationToken);

    void IWindowsOwnerTransferAssociationBoundary.VerifyContinuation(InstallerTransactionJournal expected) =>
        VerifyContinuation(expected);

    void IWindowsOwnerTransferCertificateBoundary.VerifyAssociation(InstallerMachineAssociation expected) =>
        VerifyAssociation(expected);

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

    internal void PinStartupContinuation(InstallerTransactionJournal expected, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_purpose != Purpose.StartupBlock || _startupContinuationPath is null)
        {
            throw new InstallerProtocolException("installer.owner_transfer.startup_phase_invalid");
        }
        if (!_paths.Contains(_startupContinuationPath))
        {
            AcquireNode(new Spec(_startupContinuationPath, Role.ContinuationFile), 3, cancellationToken);
        }
        VerifyContinuation(expected);
    }

    internal void ApplyAndVerify(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_purpose is not (Purpose.MachineAccess or Purpose.InstallerAccess))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_read_only");
        }
        Reverify(requireTransferred: false, cancellationToken);
        foreach (Node node in _nodes.Where(node => CanChange(node.Spec)))
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
        IWindowsOwnerTransferAccessLease lease = _native.Open(spec.Path, spec.Directory, CanChange(spec));
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
        Validate(node, requireTransferred: RequireTransferredOnAcquisition);
        // The protected Installer subtree is opaque except for this fixed, read-only barrier.
        // A normal reader requires the old product-root ACL and cannot span its transfer.
        if (!InspectInstallerTree && spec.Role is (Role.InstallerBoundary or Role.ContinuationDirectory))
        {
            bool installer = spec.Role == Role.InstallerBoundary;
            AcquireNode(new Spec(Path.Combine(spec.Path, installer
                ? InstallerStateLayout.VersionDirectoryName : InstallerStateLayout.JournalFileName),
                installer ? Role.ContinuationDirectory : Role.ContinuationFile), checked(depth + 1), cancellationToken);
        }
        if ((_purpose == Purpose.Certificates || InspectInstallerTree) && spec.Role == Role.AuthorityBoundary)
        {
            // The caller already persisted its private journal here. Read-only acquisition must
            // never create a missing private state root or enumerate unrelated account archives.
            AcquireNode(new Spec(Path.Combine(spec.Path, InstallerOwnerTransferStateLayout.VersionDirectoryName),
                Role.CertificateArchiveDirectory), checked(depth + 1), cancellationToken);
        }
        if (!spec.Enumerate && !(InspectInstallerTree && spec.Role is (Role.InstallerBoundary or Role.ContinuationDirectory)))
        {
            return;
        }

        node.Children = ReadChildren(spec.Path, cancellationToken);
        ValidateFixedChildren(spec, node.Children);
        foreach ((string childPath, bool directory) in node.Children.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            Spec child = DescribeChild(spec, childPath, directory);
            if (_associationTemporaryPath is not null && child.Role == Role.Association)
            {
                // The dedicated file port pins and validates this leaf for every read, releases
                // it for the atomic rename, then reopens it. All parent/barrier leases stay held.
                continue;
            }
            AcquireNode(child, checked(depth + 1), cancellationToken);
        }
    }

    private Dictionary<string, bool> ReadChildren(string path, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int observed = 0;
        foreach (WindowsOwnerTransferAccessEntry entry in _native.EnumerateChildren(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++observed > MaximumEntries)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_tree_limit");
            }
            ValidateChildPath(path, entry.Path);
            if (_startupContinuationPath is not null && string.Equals(entry.Path, _startupContinuationPath, StringComparison.Ordinal))
            {
                if (entry.IsDirectory)
                {
                    throw new InstallerProtocolException("installer.owner_transfer.access_object_invalid");
                }
                // Only the exact ordinary Prepared leaf may appear while its guarded store writes.
                // It is pinned and independently checked before this phase can complete.
                continue;
            }
            if (!entry.IsDirectory && string.Equals(entry.Path, _associationTemporaryPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(entry.Path), Path.GetFileName(_associationTemporaryPath), StringComparison.Ordinal))
            {
                // Only this durable transaction's temporary may appear/disappear during replacement.
                // Its kind, ACL, link count and exact new-byte prefix are checked by the file port.
                continue;
            }
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
            Role.InstallerBoundary or Role.ContinuationDirectory or Role.ContinuationFile or Role.ActiveCertificateFile =>
                HasInstallerAccess(node.Spec, security, requireTransferred),
            Role.AuthorityBoundary or Role.CertificateArchiveDirectory =>
                WindowsOwnerTransferAccessPolicy.HasPrivateDirectoryAccess(security, service: false),
            Role.ServiceBoundary => WindowsOwnerTransferAccessPolicy.HasPrivateDirectoryAccess(security, service: true),
            _ when _purpose == Purpose.StartupBlock => WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
                security, _previousSid, node.Spec.Directory, node.Spec.Inherited),
            _ => WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
                    security, _nextSid, node.Spec.Directory, node.Spec.Inherited)
                || (_purpose == Purpose.MachineAccess && !requireTransferred && WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
                    security, _previousSid, node.Spec.Directory, node.Spec.Inherited)),
        };
        if (!accepted || (node.Spec.PreservedBoundary && !CanChange(node.Spec) && !SameSecurity(node.OriginalSecurity, security)))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_acl_invalid");
        }
    }

    private bool RequireTransferredOnAcquisition => _purpose is not (Purpose.MachineAccess or Purpose.InstallerAccess);
    private bool InspectInstallerTree => _purpose is Purpose.StartupBlock or Purpose.InstallerAccess or Purpose.Completed;

    private bool CanChange(Spec spec) => _purpose switch
    {
        Purpose.MachineAccess => spec.CanChange,
        Purpose.InstallerAccess => spec.InstallerState,
        _ => false,
    };

    private bool HasInstallerAccess(Spec spec, WindowsDirectorySecuritySnapshot security, bool requireTransferred) =>
        InspectInstallerTree && _purpose != Purpose.StartupBlock
            ? WindowsOwnerTransferAccessPolicy.HasOwnerAccess(security, _nextSid, spec.Directory, spec.Inherited)
                || (_purpose == Purpose.InstallerAccess && !requireTransferred
                    && WindowsOwnerTransferAccessPolicy.HasOwnerAccess(security, _previousSid, spec.Directory, spec.Inherited))
            : WindowsOwnerTransferAccessPolicy.HasOwnerAccess(security, _previousSid, spec.Directory, spec.Inherited);

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

    private void ValidateFixedChildren(Spec parent, IReadOnlyDictionary<string, bool> children)
    {
        string[] required = parent.Role switch
        {
            Role.ProgramFilesProduct => ["Service"],
            Role.ProgramDataProduct => ["MihomoService", "Installer", "InstallerAuthority"],
            Role.ServiceDataRoot => ["association.json"],
            Role.InstallerBoundary => [InstallerStateLayout.VersionDirectoryName],
            Role.ContinuationDirectory when _purpose != Purpose.StartupBlock => [InstallerStateLayout.JournalFileName],
            _ => [],
        };
        foreach (string name in required)
        {
            if (!children.ContainsKey(Path.Combine(parent.Path, name)))
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_required_state_missing");
            }
        }
        if (parent.Role == Role.ContinuationDirectory
            && children.ContainsKey(Path.Combine(parent.Path, FileInstallerCertificateOwnershipStore.LedgerFileName)) != _activeCertificatePresent)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_state_conflict");
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
            Role.InstallerBoundary when directory && name.Equals(InstallerStateLayout.VersionDirectoryName, StringComparison.OrdinalIgnoreCase) =>
                Role.ContinuationDirectory,
            Role.ContinuationDirectory when !directory && name.Equals(InstallerStateLayout.JournalFileName, StringComparison.OrdinalIgnoreCase) =>
                Role.ContinuationFile,
            Role.ContinuationDirectory when !directory && name.Equals(FileInstallerCertificateOwnershipStore.LedgerFileName, StringComparison.OrdinalIgnoreCase) =>
                Role.ActiveCertificateFile,
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
        ContinuationDirectory, ContinuationFile, CertificateArchiveDirectory, ActiveCertificateFile,
    }

    private enum Purpose { MachineAccess, Association, Certificates, InstallerAccess, Completed, StartupBlock }

    private sealed record Spec(string Path, Role Role)
    {
        internal bool Directory => Role is not (Role.PayloadFile or Role.Association or Role.ContinuationFile or Role.ActiveCertificateFile);
        internal bool PreservedBoundary => Role is Role.InstallerBoundary or Role.AuthorityBoundary or Role.ServiceBoundary
            or Role.ContinuationDirectory or Role.ContinuationFile or Role.CertificateArchiveDirectory or Role.ActiveCertificateFile;
        internal bool CanChange => Role != Role.Anchor && !PreservedBoundary;
        internal bool Inherited => Role is Role.PayloadDirectory or Role.PayloadFile or Role.Association or Role.ContinuationFile or Role.ActiveCertificateFile;
        internal bool Enumerate => Directory && CanChange;
        internal bool InstallerState => Role is Role.InstallerBoundary or Role.ContinuationDirectory or Role.ContinuationFile or Role.ActiveCertificateFile;
    }

    private sealed class Node(Spec spec, IWindowsOwnerTransferAccessLease lease, WindowsDirectorySecuritySnapshot originalSecurity)
    {
        internal Spec Spec { get; } = spec;
        internal IWindowsOwnerTransferAccessLease Lease { get; } = lease;
        internal WindowsDirectorySecuritySnapshot OriginalSecurity { get; } = originalSecurity;
        internal Dictionary<string, bool>? Children { get; set; }
    }
}

namespace ClashSharp.Installer.Contracts;

/// <summary>Fixed product directories; never accepts a caller-supplied filesystem path.</summary>
public enum InstallerDirectoryRole
{
    /// <summary>The product directory beneath Program Files.</summary>
    ProgramFilesProduct,
    /// <summary>The product directory beneath ProgramData.</summary>
    ProgramDataProduct,
    /// <summary>The Installer parent directory.</summary>
    InstallerRoot,
    /// <summary>The versioned ordinary transaction directory.</summary>
    InstallerVersion,
    /// <summary>The InstallerAuthority parent directory.</summary>
    AuthorityRoot,
    /// <summary>The versioned private authority directory.</summary>
    AuthorityVersion,
}

/// <summary>Independently observed final disposition of one fixed directory.</summary>
public enum InstallerDirectoryCleanupDisposition
{
    /// <summary>The directory was already absent.</summary>
    Missing,
    /// <summary>The owned empty directory was deleted and its absence confirmed.</summary>
    Deleted,
    /// <summary>The directory remains because durable creation ownership was not established.</summary>
    RetainedUnprovenOwnership,
    /// <summary>The owned directory remains because it contains other entries.</summary>
    RetainedNonEmpty,
}

/// <summary>One path-free terminal directory observation.</summary>
/// <param name="Role">Fixed directory role.</param>
/// <param name="Disposition">Verified deletion, absence, or explicit preservation.</param>
public sealed record InstallerDirectoryCleanupEntry(
    InstallerDirectoryRole Role,
    InstallerDirectoryCleanupDisposition Disposition);

/// <summary>Immutable, complete observations of the six product directories after uninstall.</summary>
public sealed class InstallerDirectoryCleanupReport : IEquatable<InstallerDirectoryCleanupReport>
{
    /// <summary>The exact number of fixed directory roles in this protocol.</summary>
    public const int DirectoryCount = 6;

    private readonly InstallerDirectoryCleanupEntry[] _entries;

    /// <summary>Copies and validates a bounded, complete set of observations.</summary>
    public InstallerDirectoryCleanupReport(IEnumerable<InstallerDirectoryCleanupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = new List<InstallerDirectoryCleanupEntry>(DirectoryCount);
        foreach (InstallerDirectoryCleanupEntry entry in entries)
        {
            if (snapshot.Count == DirectoryCount || entry is null)
            {
                throw Invalid();
            }
            snapshot.Add(entry);
        }

        _entries = snapshot.OrderBy(static entry => entry.Role).ToArray();
        Entries = Array.AsReadOnly(_entries);
        Validate();
    }

    /// <summary>Gets the copied observations in canonical directory-role order.</summary>
    public IReadOnlyList<InstallerDirectoryCleanupEntry> Entries { get; }

    /// <summary>Gets whether any directory was explicitly preserved.</summary>
    public bool HasRetained => _entries.Any(static entry => entry.Disposition is
        InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership
        or InstallerDirectoryCleanupDisposition.RetainedNonEmpty);

    /// <summary>Rejects missing, duplicated, or unknown roles and unknown dispositions.</summary>
    public void Validate()
    {
        if (_entries.Length != DirectoryCount)
        {
            throw Invalid();
        }
        for (int index = 0; index < DirectoryCount; index++)
        {
            if ((int)_entries[index].Role != index || !Enum.IsDefined(_entries[index].Disposition))
            {
                throw Invalid();
            }
        }
    }

    /// <inheritdoc />
    public bool Equals(InstallerDirectoryCleanupReport? other) =>
        other is not null && _entries.SequenceEqual(other._entries);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InstallerDirectoryCleanupReport other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (InstallerDirectoryCleanupEntry entry in _entries)
        {
            hash.Add(entry);
        }
        return hash.ToHashCode();
    }

    private static InstallerProtocolException Invalid() => new("installer.directory_cleanup.report_invalid");
}

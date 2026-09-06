using System.ComponentModel;
using System.Security.Principal;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.MihomoService;

/// <summary>
/// Pins and observes the Installer-owned shared roots without creating directories or changing
/// their descriptors. The service may create private descendants only while this lease is held.
/// </summary>
internal sealed class MihomoServiceSharedDirectoryGuard
{
    private readonly string _targetSid;
    private readonly (string Path, bool ExactOwnerAccess)[] _paths;
    private readonly Func<string, IWindowsDirectoryReadLease> _open;

    internal MihomoServiceSharedDirectoryGuard(
        string commonApplicationData,
        string targetSid,
        Func<string, IWindowsDirectoryReadLease>? open = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonApplicationData);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSid);
        string canonical = Path.GetFullPath(commonApplicationData);
        string volume = Path.GetPathRoot(canonical) ?? string.Empty;
        var account = new SecurityIdentifier(targetSid);
        if (volume.Length != 3 || volume[1] != ':'
            || !Path.IsPathFullyQualified(commonApplicationData)
            || !account.IsAccountSid()
            || !string.Equals(account.Value, targetSid, StringComparison.Ordinal))
        {
            throw new ArgumentException("Canonical local machine roots and an account SID are required.");
        }

        _targetSid = targetSid;
        _open = open ?? WindowsDirectoryReadLease.Open;
        var paths = new List<(string Path, bool ExactOwnerAccess)> { (volume, false) };
        string current = volume;
        foreach (string segment in canonical[volume.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            paths.Add((current, false));
        }
        if (paths.Count > 64)
        {
            throw new ArgumentException("The machine directory chain is too deep.", nameof(commonApplicationData));
        }
        string product = Path.Combine(canonical, "ClashSharp");
        paths.Add((product, true));
        paths.Add((Path.Combine(product, "MihomoService"), true));
        _paths = [.. paths];
    }

    internal IDisposable Acquire()
    {
        var acquired = new List<IWindowsDirectoryReadLease>(_paths.Length);
        try
        {
            foreach ((string path, bool exact) in _paths)
            {
                IWindowsDirectoryReadLease lease = _open(path);
                acquired.Add(lease);
                Validate(lease.Observe(), exact);
            }
            for (int index = 0; index < acquired.Count; index++)
            {
                Validate(acquired[index].Observe(), _paths[index].ExactOwnerAccess);
            }
            return new Lifetime(acquired);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Dispose(acquired);
            throw new MihomoServiceConfigurationTrustException(
                "The Installer-owned shared service directories are missing or unsafe.");
        }
        catch
        {
            Dispose(acquired);
            throw;
        }
    }

    private void Validate(WindowsDirectoryObservation observation, bool exact)
    {
        if (!observation.IsDirectory || observation.IsReparsePoint
            || !(exact
                ? WindowsDirectoryAccessPolicy.HasExactOwnerReadOnlyAccess(observation.Security, _targetSid)
                : WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(observation.Security)))
        {
            throw new MihomoServiceConfigurationTrustException(
                "The Installer-owned shared service directory does not match its owner.");
        }
    }

    private static void Dispose(IReadOnlyList<IWindowsDirectoryReadLease> leases)
    {
        for (int index = leases.Count - 1; index >= 0; index--)
        {
            leases[index].Dispose();
        }
    }

    private sealed class Lifetime(IReadOnlyList<IWindowsDirectoryReadLease> leases) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                MihomoServiceSharedDirectoryGuard.Dispose(leases);
                _disposed = true;
            }
        }
    }
}

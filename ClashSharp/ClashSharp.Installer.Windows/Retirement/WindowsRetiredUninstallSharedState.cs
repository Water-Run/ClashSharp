using System.ComponentModel;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Transactions;
using ClashSharp.Windows.FileSecurity;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Retirement;

internal interface IWindowsRetiredUninstallSharedState : IDisposable
{
    Task ReverifyAsync(CancellationToken cancellationToken);
}

/// <summary>Only observations are available to this capability; it cannot create directories or mutate SCM.</summary>
internal interface IWindowsRetiredUninstallObservationNative
{
    IWindowsInstallerDirectoryLease OpenDirectory(string path);
    WindowsMachineAssociationFileObservation ReadAssociation(string path, string ownerSid);
    bool IsEntryPresent(string path);
    string ResolveProfile(string targetSid, CancellationToken cancellationToken);
    void VerifyServiceAbsent(CancellationToken cancellationToken);
}

/// <summary>
/// Pins the fixed shared roots and proves that the authenticated account is no longer their owner.
/// Existing roots retain their exact owner-readable policy; missing roots remain absent. The
/// ordinary journal must be absent even when the shared service has already been uninstalled.
/// No new-owner App barrier is acquired, so its running application can continue using the service.
/// The caller owns machine exclusion and the retired account's App barrier until disposal.
/// </summary>
internal sealed class WindowsRetiredUninstallSharedState : IWindowsRetiredUninstallSharedState
{
    private readonly string _targetSid;
    private readonly string _programData;
    private readonly string _programFiles;
    private readonly IWindowsRetiredUninstallObservationNative _native;
    private readonly List<Pin> _pins = [];
    private string? _profile;
    private AssociationIdentity? _association;
    private bool _captured;
    private bool _disposed;

    internal WindowsRetiredUninstallSharedState(string targetSid, string programData, string programFiles,
        IWindowsRetiredUninstallObservationNative native)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        ArgumentNullException.ThrowIfNull(native);
        _targetSid = targetSid;
        _programData = Normalize(programData);
        _programFiles = Normalize(programFiles);
        _native = native;
    }

    internal static WindowsRetiredUninstallSharedState CreateDefault(string targetSid) => new(targetSid,
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify),
        new WindowsRetiredUninstallObservationNative());

    public Task ReverifyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!_captured)
            {
                CaptureChain(_programData, ["ClashSharp", "Installer", "v2"], cancellationToken);
                CaptureChain(_programData, ["ClashSharp", "MihomoService"], cancellationToken);
                CaptureChain(_programFiles, ["ClashSharp", "Service"], cancellationToken);
                _profile = _native.ResolveProfile(_targetSid, cancellationToken);
                _association = ReadAssociation(cancellationToken);
                _captured = true;
            }
            foreach (Pin pin in _pins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pin.Lease is { } lease)
                {
                    Validate(lease.Observe(), pin.ExactOwner, pin.Protected);
                }
                else if (_native.IsEntryPresent(pin.Path))
                {
                    throw Changed();
                }
            }
            if (_native.ResolveProfile(_targetSid, cancellationToken) != _profile
                || ReadAssociation(cancellationToken) != _association)
            {
                throw Changed();
            }
            string[] owners = _pins.Where(pin => pin.ExactOwner is not null)
                .Select(pin => pin.ExactOwner!).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length > 1)
            {
                throw Changed();
            }
            if (_association is { } association)
            {
                if (association.OwnerSid == _targetSid)
                {
                    throw new InstallerProtocolException("installer.retired_uninstall.current_owner");
                }
                if (owners.Length != 1 || owners[0] != association.OwnerSid
                    || _pins.Any(pin => pin.Protected && pin.Lease is null))
                {
                    throw Changed();
                }
            }
            else
            {
                // Absence of association alone cannot authorize retiring a still-installed service.
                _native.VerifyServiceAbsent(cancellationToken);
            }
            string ordinaryRoot = Path.Combine(_programData, "ClashSharp", "Installer", "v2");
            if (_pins.Single(pin => pin.Path == ordinaryRoot).Lease is not null
                && _native.IsEntryPresent(Path.Combine(ordinaryRoot, InstallerStateLayout.JournalFileName)))
            {
                throw new InstallerProtocolException("installer.retired_uninstall.ordinary_state_pending");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.shared_inspection_failed");
        }
    }

    private void CaptureChain(string knownRoot, string[] descendants, CancellationToken cancellationToken)
    {
        string volume = Path.GetPathRoot(knownRoot)!;
        var paths = new List<(string Path, bool Protected, bool MissingAllowed)> { (volume, false, false) };
        string current = volume;
        foreach (string part in Path.GetRelativePath(volume, knownRoot).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".")
            {
                continue;
            }
            current = Path.Combine(current, part);
            paths.Add((current, false, false));
        }
        for (int index = 0; index < descendants.Length; index++)
        {
            current = Path.Combine(current, descendants[index]);
            paths.Add((current, index > 0, true));
        }
        bool missingParent = false;
        foreach (var spec in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pin? existing = _pins.SingleOrDefault(pin => string.Equals(pin.Path, spec.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                missingParent = existing.Lease is null;
                continue;
            }
            IWindowsInstallerDirectoryLease? lease = null;
            try
            {
                if (!missingParent)
                {
                    try { lease = _native.OpenDirectory(spec.Path); }
                    catch (Exception exception) when (spec.MissingAllowed && IsMissing(exception))
                    {
                        missingParent = true;
                    }
                }
                string? owner = null;
                if (lease is not null)
                {
                    WindowsDirectoryObservation observation = lease.Observe();
                    if (spec.Protected)
                    {
                        string[] candidates = observation.Security.AccessEntries.Select(entry => entry.Sid)
                            .Where(sid => sid is not (WindowsDirectoryAccessPolicy.LocalSystemSid or WindowsDirectoryAccessPolicy.AdministratorsSid))
                            .Distinct(StringComparer.Ordinal).ToArray();
                        if (candidates.Length != 1)
                        {
                            throw Changed();
                        }
                        owner = candidates[0];
                        InstallerProtocolValidation.ValidateTargetSid(owner);
                    }
                    Validate(observation, owner, spec.Protected);
                }
                _pins.Add(new(spec.Path, lease, owner, spec.Protected));
                lease = null;
            }
            finally { lease?.Dispose(); }
        }
    }

    private AssociationIdentity? ReadAssociation(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.Combine(_programData, "ClashSharp", "MihomoService");
        Pin rootPin = _pins.Single(pin => pin.Path == root);
        if (rootPin.Lease is null)
        {
            return null;
        }
        WindowsMachineAssociationFileObservation observation = _native.ReadAssociation(Path.Combine(root, "association.json"), rootPin.ExactOwner!);
        byte[]? canonical = null;
        try
        {
            observation.Validate();
            if (observation.Status == WindowsMachineAssociationFileStatus.Missing)
            {
                return null;
            }
            if (observation.Status != WindowsMachineAssociationFileStatus.OrdinaryFile)
            {
                throw Changed();
            }
            InstallerMachineAssociation association = InstallerMachineAssociationCodec.Parse(observation.Bytes!);
            canonical = InstallerMachineAssociationCodec.Serialize(association);
            if (!CryptographicOperations.FixedTimeEquals(observation.Bytes!, canonical))
            {
                throw Changed();
            }
            return new(association.OwnerSid, Convert.ToHexStringLower(SHA256.HashData(canonical)));
        }
        catch (InstallerProtocolException)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.association_invalid");
        }
        finally
        {
            if (observation.Bytes is { } bytes)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            if (canonical is not null)
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
        }
    }

    private static void Validate(WindowsDirectoryObservation observation, string? owner, bool exact)
    {
        if (!observation.IsDirectory || observation.IsReparsePoint)
        {
            throw Changed();
        }
        if (exact)
        {
            WindowsInstallerDirectorySecurityPolicy.ValidateProtectedRoot(observation.Security, owner!);
        }
        else
        {
            WindowsInstallerDirectorySecurityPolicy.ValidateRenameAnchor(observation.Security);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            for (int index = _pins.Count - 1; index >= 0; index--)
            {
                _pins[index].Lease?.Dispose();
            }
            _pins.Clear();
        }
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] != '\\' || path.AsSpan(2).Contains(':')
            || Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != Path.TrimEndingDirectorySeparator(path))
        {
            throw new InstallerProtocolException("installer.retired_uninstall.root_invalid");
        }
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static bool IsMissing(Exception exception) => exception is FileNotFoundException or DirectoryNotFoundException
        or Win32Exception { NativeErrorCode: 2 or 3 };
    private static InstallerProtocolException Changed() => new("installer.retired_uninstall.shared_state_changed");
    private sealed record Pin(string Path, IWindowsInstallerDirectoryLease? Lease, string? ExactOwner, bool Protected);
    private sealed record AssociationIdentity(string OwnerSid, string Digest);
}

internal sealed class WindowsRetiredUninstallObservationNative : IWindowsRetiredUninstallObservationNative
{
    public IWindowsInstallerDirectoryLease OpenDirectory(string path) => new WindowsInstallerDirectoryNative().OpenDirectory(path);
    public string ResolveProfile(string targetSid, CancellationToken cancellationToken) => new WindowsTargetProfileResolver().Resolve(targetSid, cancellationToken);
    public void VerifyServiceAbsent(CancellationToken cancellationToken) => new WindowsServiceConfigurationVerifier().VerifyAbsent(cancellationToken);

    public bool IsEntryPresent(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public WindowsMachineAssociationFileObservation ReadAssociation(string path, string ownerSid)
    {
        byte[]? bytes = null;
        try
        {
            using SafeFileHandle file = WindowsFileSystemNative.OpenOrdinaryFile(path);
            if (WindowsFileSystemNative.GetLinkCount(file) != 1
                || !WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
                    WindowsDirectoryReadLease.ReadSecuritySnapshot(file), ownerSid, directory: false, inherited: true))
            {
                return new(WindowsMachineAssociationFileStatus.Unsafe, null);
            }
            long length = RandomAccess.GetLength(file);
            if (length is <= 0 or > InstallerMachineAssociationCodec.MaximumAssociationBytes)
            {
                return new(WindowsMachineAssociationFileStatus.Unsafe, null);
            }
            bytes = new byte[checked((int)length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = RandomAccess.Read(file, bytes.AsSpan(offset), offset);
                if (count == 0)
                {
                    return new(WindowsMachineAssociationFileStatus.Unsafe, null);
                }
                offset += count;
            }
            var observation = new WindowsMachineAssociationFileObservation(WindowsMachineAssociationFileStatus.OrdinaryFile, bytes);
            bytes = null;
            return observation;
        }
        catch (Exception exception) when (exception is Win32Exception { NativeErrorCode: 2 or 3 }
            or FileNotFoundException or DirectoryNotFoundException)
        {
            return new(WindowsMachineAssociationFileStatus.Missing, null);
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }
}

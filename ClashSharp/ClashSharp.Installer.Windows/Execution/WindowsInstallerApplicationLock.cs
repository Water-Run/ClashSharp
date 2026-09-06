using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Execution;

internal interface IWindowsInstallerApplicationLock
{
    IDisposable Acquire(string targetSid, CancellationToken cancellationToken);
}

/// <summary>
/// Holds read-only coordination handles that exclude the App's exclusive lifetime lock. The parent
/// may create its own empty lock; the elevated helper can only open an existing target-user lock.
/// </summary>
internal sealed class WindowsInstallerApplicationLock : IWindowsInstallerApplicationLock
{
    private readonly bool _createCurrentUserLock;
    private readonly Func<string, CancellationToken, string> _resolveProfile;
    private readonly Func<string> _currentSid;
    private readonly Func<string> _currentLocalData;

    internal WindowsInstallerApplicationLock(
        bool createCurrentUserLock,
        Func<string, CancellationToken, string> resolveProfile,
        Func<string> currentSid,
        Func<string> currentLocalData)
    {
        ArgumentNullException.ThrowIfNull(resolveProfile);
        ArgumentNullException.ThrowIfNull(currentSid);
        ArgumentNullException.ThrowIfNull(currentLocalData);
        _createCurrentUserLock = createCurrentUserLock;
        _resolveProfile = resolveProfile;
        _currentSid = currentSid;
        _currentLocalData = currentLocalData;
    }

    internal static WindowsInstallerApplicationLock CreateCurrentUser() => Create(createCurrentUserLock: true);

    internal static WindowsInstallerApplicationLock CreateHelper() => Create(createCurrentUserLock: false);

    public IDisposable Acquire(string targetSid, CancellationToken cancellationToken)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        cancellationToken.ThrowIfCancellationRequested();
        List<SafeFileHandle> handles = [];
        try
        {
            if (_createCurrentUserLock
                && !string.Equals(_currentSid(), targetSid, StringComparison.Ordinal))
            {
                throw new InstallerProtocolException("installer.environment.target_user_mismatch");
            }

            string profile = _resolveProfile(targetSid, cancellationToken);
            ValidateProfilePath(profile);
            string localData = Path.Combine(profile, "AppData", "Local");
            string directory = Path.Combine(localData, InstallerStateLayout.ProductDirectoryName);
            string lockPath = Path.Combine(directory, InstallerStateLayout.ApplicationMutationLockFileName);
            if (_createCurrentUserLock)
            {
                if (!string.Equals(_currentLocalData(), localData, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InstallerProtocolException("installer.application_lock.profile_mismatch");
                }
            }

            // Open each component without following reparse points and retain its rename fence.
            // The helper never creates a directory or file in a user-controlled path.
            string current = Path.GetPathRoot(localData)!;
            handles.Add(WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(current));
            foreach (string segment in localData[current.Length..].Split(Path.DirectorySeparatorChar))
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = Path.Combine(current, segment);
                handles.Add(WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(current));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_createCurrentUserLock)
            {
                // Creation is confined to the already pinned, ordinary current-user known folder.
                Directory.CreateDirectory(directory);
            }
            handles.Add(WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(directory));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                handles.Add(_createCurrentUserLock
                    ? WindowsFileSystemNative.OpenOrCreateOrdinaryFile(lockPath)
                    : WindowsFileSystemNative.OpenOrdinaryFile(lockPath));
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 32 or 33)
            {
                throw new InstallerProtocolException("installer.application_running", exception);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ApplicationLockLease(handles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            DisposeHandles(handles);
            throw new InstallerProtocolException("installer.application_lock.unavailable", exception);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    private static WindowsInstallerApplicationLock Create(bool createCurrentUserLock) => new(
        createCurrentUserLock,
        new WindowsTargetProfileResolver().Resolve,
        WindowsInstallerCurrentUser.GetSid,
        static () => Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify));

    private static void ValidateProfilePath(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)
            || profile.Length < 4
            || !char.IsAsciiLetter(profile[0])
            || profile[1] != ':'
            || profile[2] != Path.DirectorySeparatorChar
            || profile.AsSpan(2).Contains(':')
            || profile.Contains('/', StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(profile)
            || !string.Equals(profile, Path.GetFullPath(profile), StringComparison.OrdinalIgnoreCase)
            || profile.EndsWith(Path.DirectorySeparatorChar))
        {
            throw new InstallerProtocolException("installer.application_lock.profile_invalid");
        }
    }

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (int index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }

    private sealed class ApplicationLockLease(List<SafeFileHandle> handles) : IDisposable
    {
        private List<SafeFileHandle>? _handles = handles;

        public void Dispose()
        {
            List<SafeFileHandle>? owned = Interlocked.Exchange(ref _handles, null);
            if (owned is not null)
            {
                DisposeHandles(owned);
            }
        }
    }
}

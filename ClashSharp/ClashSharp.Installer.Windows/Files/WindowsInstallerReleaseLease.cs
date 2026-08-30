using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Files;

internal sealed class WindowsInstallerReleaseLease : IInstallerReleaseLease
{
    private readonly InstallerRequest _request;
    private readonly string? _payloadRoot;
    private readonly WindowsLockedPayloadFile[] _lockedFiles;
    private readonly WindowsLockedDirectory[] _directoryGuards;
    private bool _disposed;

    internal WindowsInstallerReleaseLease(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        string? payloadRoot,
        IReadOnlyList<WindowsLockedPayloadFile> lockedFiles,
        IReadOnlyList<WindowsLockedDirectory> directoryGuards)
    {
        _request = request;
        Manifest = manifest;
        _payloadRoot = payloadRoot;
        _lockedFiles = lockedFiles.ToArray();
        _directoryGuards = directoryGuards.ToArray();
        LockedFiles = _lockedFiles;
        Release = manifest.CreateVerifiedRelease(
            packagePayloadAvailable: payloadRoot is not null,
            certificatePayloadAvailable: payloadRoot is not null);
    }

    public VerifiedInstallerRelease Release { get; }

    public InstallerReleaseManifest Manifest { get; }

    public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles { get; }

    public Task ReverifyAsync(
        InstallerRequest request,
        CancellationToken cancellationToken)
    {
        RequireRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (_payloadRoot is null)
        {
            if (request.Operation != InstallerOperation.Uninstall || _lockedFiles.Length != 0)
            {
                throw new InstallerProtocolException("installer.release.locked_file_set_invalid");
            }

            return Task.CompletedTask;
        }

        try
        {
            WindowsInstallerPayloadLocker.VerifyExactShape(_payloadRoot, Manifest, cancellationToken);
            foreach (WindowsLockedDirectory directory in _directoryGuards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                directory.Reverify();
            }

            foreach (WindowsLockedPayloadFile file in _lockedFiles)
            {
                file.Reverify(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            throw new InstallerProtocolException(
                "installer.release.payload_reverify_failed",
                exception);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        for (int index = _lockedFiles.Length - 1; index >= 0; index--)
        {
            _lockedFiles[index].Dispose();
        }

        for (int index = _directoryGuards.Length - 1; index >= 0; index--)
        {
            _directoryGuards[index].Dispose();
        }

        _disposed = true;
        return ValueTask.CompletedTask;
    }

    internal WindowsLockedPayloadFile RequireFile(InstallerPayloadFileRole role)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WindowsLockedPayloadFile[] matches = _lockedFiles
            .Where(file => file.ManifestEntry.Role == role)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InstallerProtocolException("installer.release.locked_file_set_invalid");
    }

    internal WindowsLockedPayloadFile RequireFile(InstallerPayloadFileEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        entry.Validate();
        WindowsLockedPayloadFile[] matches = _lockedFiles
            .Where(file => file.ManifestEntry == entry)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InstallerProtocolException("installer.release.locked_file_set_invalid");
    }

    internal void RequireRequest(InstallerRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request != _request)
        {
            throw new InstallerProtocolException("installer.release.request_changed");
        }
    }
}

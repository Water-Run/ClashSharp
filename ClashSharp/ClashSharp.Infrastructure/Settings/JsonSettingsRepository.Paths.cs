using ClashSharp.Infrastructure.Data;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Infrastructure.Settings;

public sealed partial class JsonSettingsRepository
{
    internal void EnsureLayout()
    {
        if (!Directory.Exists(Generation.RootPath))
        {
            throw new DirectoryNotFoundException(
                "The pinned data-generation root no longer exists.");
        }

        DataGenerationIdentityMarker.Validate(Generation);
        if (!DataGenerationPathPolicy.IsContainedBy(
                Generation.RootPath,
                SettingsDirectoryPath))
        {
            throw new IOException(
                "The settings repository escapes its data generation.");
        }

        DataGenerationPathPolicy.EnsureDirectoryHierarchy(
            SettingsDirectoryPath,
            Directory.Exists,
            File.Exists,
            static path => Directory.CreateDirectory(path),
            File.GetAttributes);
        ValidateExistingPath(SettingsDirectoryPath);
        ValidateKnownFile(PrimaryPath);
        ValidateKnownFile(BackupPath);
        ValidateKnownFile(LockPath);
        DataGenerationIdentityMarker.Validate(Generation);
    }

    private async Task<FileStream> AcquireRepositoryLockAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SafeFileHandle handle =
                    ReparseSafeFile.OpenWriteLock(LockPath);
                try
                {
                    ValidateExistingPath(LockPath);
                    DataGenerationIdentityMarker.Validate(Generation);
                    return new FileStream(
                        handle,
                        FileAccess.Write,
                        bufferSize: 1,
                        isAsync: false);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(10),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private void CleanupCandidates()
    {
        foreach (string path in Directory.EnumerateFiles(
                     SettingsDirectoryPath,
                     "*.candidate.*",
                     SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith(
                    PrimaryFileName + ".candidate.",
                    StringComparison.Ordinal)
                || fileName.StartsWith(
                    BackupFileName + ".candidate.",
                    StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
    }

    private static void ValidateKnownFile(string path)
    {
        if (File.Exists(path))
        {
            ValidateExistingPath(path);
        }
    }

    private static void ValidateExistingPath(string path)
    {
        DataGenerationPathPolicy.ValidateNoReparsePoints(
            path,
            File.GetAttributes);
    }

    private static bool IsSharingViolation(IOException exception)
    {
        int nativeError = exception.HResult & 0xFFFF;
        return nativeError is 32 or 33
            || exception.InnerException is System.ComponentModel.Win32Exception
            {
                NativeErrorCode: 32 or 33,
            };
    }
}

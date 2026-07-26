using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Settings;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Infrastructure.Settings;

public sealed partial class JsonSettingsRepository
{
    private const int MaximumEnvelopeBytes = 16 * 1024 * 1024;

    private async Task<SettingsPersistenceResult> OpenCoreAsync(
        CancellationToken cancellationToken)
    {
        SettingsFileReadResult primary =
            await ReadFileAsync(PrimaryPath, cancellationToken)
                .ConfigureAwait(false);
        if (primary.Kind == SettingsFileReadKind.Valid)
        {
            return SettingsPersistenceResult.Succeeded(primary.Envelope);
        }

        SettingsFileReadResult backup =
            await ReadFileAsync(BackupPath, cancellationToken)
                .ConfigureAwait(false);
        if (backup.Kind == SettingsFileReadKind.Valid)
        {
            if (primary.Kind == SettingsFileReadKind.Corrupt)
            {
                Quarantine(PrimaryPath);
            }

            await RestorePrimaryAsync(backup.Bytes!, cancellationToken)
                .ConfigureAwait(false);
            return SettingsPersistenceResult.Succeeded(
                backup.Envelope,
                recoveredFromBackup: true,
                new SettingsPersistenceDiagnostic(
                    "settings.persistence.recovered_from_backup",
                    PrimaryPath));
        }

        if (primary.Kind == SettingsFileReadKind.Missing
            && backup.Kind == SettingsFileReadKind.Missing)
        {
            return SettingsPersistenceResult.Succeeded();
        }

        if (primary.Kind == SettingsFileReadKind.Corrupt)
        {
            Quarantine(PrimaryPath);
        }

        if (backup.Kind == SettingsFileReadKind.Corrupt)
        {
            Quarantine(BackupPath);
        }

        return SettingsPersistenceResult.Corrupt(
            new SettingsPersistenceDiagnostic(
                "settings.persistence.primary_and_backup_corrupt",
                SettingsDirectoryPath));
    }

    private async Task<SettingsFileReadResult> ReadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using SafeFileHandle handle = ReparseSafeFile.OpenRead(
                path,
                FileShare.ReadWrite | FileShare.Delete,
                asynchronous: true);
            ValidateExistingPath(path);
            long length = RandomAccess.GetLength(handle);
            if (length is <= 0 or > MaximumEnvelopeBytes)
            {
                return SettingsFileReadResult.Corrupt();
            }

            byte[] bytes = new byte[checked((int)length)];
            int bytesRead = 0;
            while (bytesRead < bytes.Length)
            {
                int read = await RandomAccess.ReadAsync(
                        handle,
                        bytes.AsMemory(bytesRead),
                        bytesRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return SettingsFileReadResult.Corrupt();
                }

                bytesRead += read;
            }

            SettingsEnvelope envelope =
                SettingsEnvelopeCodec.Decode(bytes, _registry);
            return SettingsFileReadResult.Valid(envelope, bytes);
        }
        catch (FileNotFoundException)
        {
            return SettingsFileReadResult.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return SettingsFileReadResult.Missing();
        }
        catch (SettingsEnvelopeCodecException)
        {
            return SettingsFileReadResult.Corrupt();
        }
    }

    private async Task RestorePrimaryAsync(
        byte[] verifiedBackupBytes,
        CancellationToken cancellationToken)
    {
        string candidate = CreateCandidatePath(PrimaryPath);
        try
        {
            await WriteAndFlushAsync(
                    candidate,
                    verifiedBackupBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await _faultInjector
                .InjectAsync(
                    SettingsPersistenceFaultPoint.BeforeEnvelopePromotion,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Promote(candidate, PrimaryPath);
            FlushPromotedFile(PrimaryPath);
            await _faultInjector
                .InjectAsync(
                    SettingsPersistenceFaultPoint.AfterEnvelopePromotion,
                    CancellationToken.None)
                .ConfigureAwait(false);
            SettingsFileReadResult restored =
                await ReadFileAsync(PrimaryPath, CancellationToken.None)
                    .ConfigureAwait(false);
            if (restored.Kind != SettingsFileReadKind.Valid
                || !restored.Bytes!.AsSpan().SequenceEqual(verifiedBackupBytes))
            {
                throw new IOException(
                    "The settings backup restoration failed re-read verification.");
            }
        }
        finally
        {
            TryDelete(candidate);
        }
    }

    private void Quarantine(string path)
    {
        try
        {
            ValidateExistingPath(path);
            string destination =
                $"{path}.corrupt.{Guid.NewGuid():N}";
            File.Move(path, destination);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private enum SettingsFileReadKind
    {
        Missing,
        Valid,
        Corrupt,
    }

    private sealed record SettingsFileReadResult(
        SettingsFileReadKind Kind,
        SettingsEnvelope? Envelope,
        byte[]? Bytes)
    {
        public static SettingsFileReadResult Missing() =>
            new(SettingsFileReadKind.Missing, null, null);

        public static SettingsFileReadResult Corrupt() =>
            new(SettingsFileReadKind.Corrupt, null, null);

        public static SettingsFileReadResult Valid(
            SettingsEnvelope envelope,
            byte[] bytes) =>
            new(SettingsFileReadKind.Valid, envelope, bytes);
    }
}

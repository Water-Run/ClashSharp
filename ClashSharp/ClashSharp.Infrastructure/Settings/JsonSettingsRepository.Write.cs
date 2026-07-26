using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Settings;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Infrastructure.Settings;

public sealed partial class JsonSettingsRepository
{
    private async Task<SettingsPersistenceResult> SaveCoreAsync(
        SettingsEnvelope envelope,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        SettingsPersistenceResult opened =
            await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        if (!opened.IsSucceeded)
        {
            return opened;
        }

        long currentRevision = opened.Envelope?.EnvelopeRevision ?? 0;
        if (currentRevision != expectedRevision)
        {
            return SettingsPersistenceResult.Conflict(opened.Envelope);
        }

        if (envelope.EnvelopeRevision != expectedRevision + 1)
        {
            return SettingsPersistenceResult.Invalid(
                new SettingsPersistenceDiagnostic(
                    "settings.persistence.revision_invalid",
                    "envelopeRevision"));
        }

        SettingsEnvelopeCodec.EncodedSettingsEnvelope encoded =
            SettingsEnvelopeCodec.Encode(envelope, _registry);
        if (opened.Envelope is not null)
        {
            byte[] currentBytes =
                SettingsEnvelopeCodec.Encode(opened.Envelope, _registry).Bytes;
            await PersistAtCutAsync(
                    BackupPath,
                    currentBytes,
                    SettingsPersistenceFaultPoint.BeforeBackupPromotion,
                    SettingsPersistenceFaultPoint.AfterBackupPromotion,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await PersistAtCutAsync(
                PrimaryPath,
                encoded.Bytes,
                SettingsPersistenceFaultPoint.BeforeEnvelopePromotion,
                SettingsPersistenceFaultPoint.AfterEnvelopePromotion,
                cancellationToken)
            .ConfigureAwait(false);

        SettingsFileReadResult verified =
            await ReadFileAsync(PrimaryPath, CancellationToken.None)
                .ConfigureAwait(false);
        if (verified.Kind != SettingsFileReadKind.Valid
            || verified.Envelope!.EnvelopeRevision != envelope.EnvelopeRevision
            || !verified.Bytes!.AsSpan().SequenceEqual(encoded.Bytes))
        {
            throw new IOException(
                "The promoted settings envelope failed re-read verification.");
        }

        return SettingsPersistenceResult.Succeeded(verified.Envelope);
    }

    private async Task PersistAtCutAsync(
        string targetPath,
        byte[] bytes,
        SettingsPersistenceFaultPoint beforePromotion,
        SettingsPersistenceFaultPoint afterPromotion,
        CancellationToken cancellationToken)
    {
        string candidate = CreateCandidatePath(targetPath);
        try
        {
            await WriteAndFlushAsync(candidate, bytes, cancellationToken)
                .ConfigureAwait(false);
            await _faultInjector
                .InjectAsync(beforePromotion, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Promote(candidate, targetPath);
            FlushPromotedFile(targetPath);
            await _faultInjector
                .InjectAsync(afterPromotion, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(candidate);
        }
    }

    private static async Task WriteAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        using SafeFileHandle handle =
            ReparseSafeFile.CreateWrite(path, asynchronous: true);
        await using FileStream stream = new(
            handle,
            FileAccess.Write,
            bufferSize: 4096,
            isAsync: true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void Promote(string candidatePath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Replace(
                candidatePath,
                targetPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: false);
            return;
        }

        File.Move(candidatePath, targetPath);
    }

    private static void FlushPromotedFile(string path)
    {
        using SafeFileHandle handle =
            ReparseSafeFile.OpenReadWrite(path, FileShare.Read);
        using FileStream stream = new(
            handle,
            FileAccess.ReadWrite,
            bufferSize: 1,
            isAsync: false);
        stream.Flush(flushToDisk: true);
    }

    private static string CreateCandidatePath(string targetPath) =>
        $"{targetPath}.candidate.{Guid.NewGuid():N}";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class CoreConfigurationService
{
    /// <summary>Reads one imported profile source while excluding concurrent replacement or deletion.</summary>
    internal async Task<string?> ReadImportedProfileConfigurationAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        string normalizedProfileId = NormalizeProfileId(profileId);
        using IDisposable transactionLease = await _profileImportGate
            .EnterAsync(normalizedProfileId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string profileConfigPath = Path.Combine(
            GetProfileDirectoryPath(normalizedProfileId),
            "config.yaml");
        lock (_syncLock)
        {
            return File.Exists(profileConfigPath)
                ? _readAllText(profileConfigPath)
                : null;
        }
    }

    /// <summary>Deletes one non-built-in imported profile directory under the managed profile root.</summary>
    internal async Task<bool> DeleteImportedProfileAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        string normalizedProfileId = NormalizeProfileId(profileId);
        if (StringComparer.Ordinal.Equals(normalizedProfileId, ProfileCatalogIds.BuiltInDirect))
        {
            throw new InvalidOperationException("The built-in direct profile cannot be deleted.");
        }

        using IDisposable transactionLease = await _profileImportGate
            .EnterAsync(normalizedProfileId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string profilesRoot = Path.GetFullPath(Path.Combine(_configurationDirectoryPath, "profiles"));
        string profileDirectory = Path.GetFullPath(Path.Combine(profilesRoot, normalizedProfileId));
        string profilesRootWithSeparator = Path.TrimEndingDirectorySeparator(profilesRoot)
            + Path.DirectorySeparatorChar;
        if (!profileDirectory.StartsWith(profilesRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The imported profile path escaped the managed profile root.");
        }

        lock (_syncLock)
        {
            if (!Directory.Exists(profileDirectory))
            {
                return false;
            }

            FileAttributes attributes = File.GetAttributes(profileDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The imported profile directory cannot be a reparse point.");
            }

            Directory.Delete(profileDirectory, recursive: true);
            return true;
        }
    }

    /// <summary>
    /// Validates and promotes one profile source, applies its derived runtime candidate, and keeps
    /// the prior source private until readiness succeeds so both layers can roll back together.
    /// </summary>
    internal async Task<ProfileRuntimeConfigurationTransactionResult> ImportAndApplyProfileConfigurationAsync(
        string profileId,
        string profileName,
        string configurationText,
        ClashSharpMode mode,
        bool effectiveTunEnabled,
        int mixedPort,
        ICoreConfigurationRuntime runtime,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(configurationText);
        ArgumentNullException.ThrowIfNull(runtime);
        if (string.IsNullOrWhiteSpace(configurationText))
        {
            throw new ArgumentException(
                "Profile configuration must not be whitespace.",
                nameof(configurationText));
        }

        string normalizedProfileId = NormalizeProfileId(profileId);
        string normalizedText = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(configurationText);
        MihomoProfileShapeValidator.Validate(normalizedText);
        string profileDirectory = GetProfileDirectoryPath(normalizedProfileId);
        string profileConfigPath = Path.Combine(profileDirectory, "config.yaml");
        string transactionId = Guid.NewGuid().ToString("N");
        string stagingPath = Path.Combine(
            profileDirectory,
            $"config.yaml.runtime-staging.{transactionId}");
        string backupPath = Path.Combine(
            profileDirectory,
            $"config.yaml.runtime-backup.{transactionId}");
        ProfileImportResult profileResult = new(
            normalizedProfileId,
            profileName.Trim(),
            profileConfigPath,
            _profileMetrics.CountNodes(normalizedText),
            _profileMetrics.CountRules(normalizedText),
            GetString("CoreConfiguration.Imported"));

        using IDisposable transactionLease = await _profileImportGate
            .EnterAsync(normalizedProfileId, cancellationToken)
            .ConfigureAwait(false);
        bool hadCommittedConfiguration = false;
        bool sourcePromoted = false;
        bool sourceRollbackFailed = false;
        try
        {
            lock (_syncLock)
            {
                Directory.CreateDirectory(profileDirectory);
                hadCommittedConfiguration = File.Exists(profileConfigPath);
                if (hadCommittedConfiguration)
                {
                    File.Copy(profileConfigPath, backupPath, overwrite: false);
                }

                _writeAllText(
                    stagingPath,
                    normalizedText,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            await _validator
                .ValidateAsync(profileDirectory, stagingPath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncLock)
            {
                File.Move(stagingPath, profileConfigPath, overwrite: true);
                sourcePromoted = true;
            }

            RuntimeConfigurationTransactionResult runtimeResult =
                await ApplyRuntimeConfigurationAsync(
                    normalizedProfileId,
                    mode,
                    effectiveTunEnabled,
                    mixedPort,
                    runtime,
                    cancellationToken).ConfigureAwait(false);
            if (!runtimeResult.IsApplied)
            {
                Exception? sourceRollbackFailure = TryRollbackProfileImport(
                    profileConfigPath,
                    stagingPath,
                    backupPath,
                    hadCommittedConfiguration,
                    commitAttempted: true);
                if (sourceRollbackFailure is not null)
                {
                    // The backup is the last recoverable copy. Do not let the
                    // outer cleanup path make a second attempt that can delete it.
                    sourceRollbackFailed = true;
                    throw new AggregateException(
                        "Runtime profile application failed and its source profile could not be restored.",
                        runtimeResult.Failure
                            ?? new InvalidOperationException("Runtime configuration did not become applied."),
                        sourceRollbackFailure);
                }

                sourcePromoted = false;
                return new ProfileRuntimeConfigurationTransactionResult(profileResult, runtimeResult);
            }

            Exception? maintenanceFailure = TryDeleteCommittedProfileBackup(backupPath);
            sourcePromoted = false;
            return new ProfileRuntimeConfigurationTransactionResult(profileResult, runtimeResult)
            {
                MaintenanceFailure = maintenanceFailure,
            };
        }
        catch (Exception transactionFailure)
        {
            if (sourceRollbackFailed)
            {
                throw;
            }

            if (!sourcePromoted)
            {
                Exception? cleanupFailure = TryRollbackProfileImport(
                    profileConfigPath,
                    stagingPath,
                    backupPath,
                    hadCommittedConfiguration,
                    commitAttempted: false);
                if (cleanupFailure is not null)
                {
                    throw new AggregateException(
                        "Profile runtime transaction failed before source promotion and cleanup also failed.",
                        transactionFailure,
                        cleanupFailure);
                }

                throw;
            }

            Exception? sourceRollbackFailure = TryRollbackProfileImport(
                profileConfigPath,
                stagingPath,
                backupPath,
                hadCommittedConfiguration,
                commitAttempted: true);
            if (sourceRollbackFailure is not null)
            {
                throw new AggregateException(
                    "Profile runtime transaction failed and its source profile could not be restored.",
                    transactionFailure,
                    sourceRollbackFailure);
            }

            throw;
        }
    }

    private static Exception? TryDeleteCommittedProfileBackup(string backupPath)
    {
        try
        {
            DeleteFileIfPresent(backupPath);
            return null;
        }
        catch (Exception maintenanceFailure)
        {
            return maintenanceFailure;
        }
    }
}

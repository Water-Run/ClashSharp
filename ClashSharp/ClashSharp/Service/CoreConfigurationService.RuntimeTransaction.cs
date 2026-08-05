using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Describes whether the manifest, applied snapshot, and live config agree.</summary>
internal readonly record struct RuntimeConfigurationIntegrityObservation(
    bool IsKnown,
    RuntimeConfigurationActivationPlan? AppliedPlan)
{
    public static RuntimeConfigurationIntegrityObservation Unknown { get; } = new(false, null);

    public static RuntimeConfigurationIntegrityObservation Inactive { get; } = new(true, null);
}

public sealed partial class CoreConfigurationService
{
    private const int RuntimeGenerationStateSchemaVersion = 1;

    private const int RuntimeSnapshotRetentionCount = 5;

    /// <summary>Serializes legacy writes and complete validate/promote/apply runtime transactions.</summary>
    private readonly SemaphoreSlim _runtimeConfigurationGate = new(1, 1);

    /// <summary>Returns the durable desired and last readiness-verified runtime generations.</summary>
    internal async Task<RuntimeConfigurationGenerationState> GetRuntimeGenerationStateAsync(
        CancellationToken cancellationToken)
    {
        await _runtimeConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_configurationDirectoryPath);
            return await LoadOrBootstrapRuntimeGenerationStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runtimeConfigurationGate.Release();
        }
    }

    /// <summary>
    /// Observes only a fully converged, hash-verified applied generation. A transaction in
    /// progress and any crash residue are deliberately reported as unknown.
    /// </summary>
    internal RuntimeConfigurationIntegrityObservation ObserveRuntimeConfigurationIntegrity()
    {
        if (!_runtimeConfigurationGate.Wait(0))
        {
            return RuntimeConfigurationIntegrityObservation.Unknown;
        }

        try
        {
            string statePath = GetRuntimeGenerationStatePath();
            if (!File.Exists(statePath))
            {
                return File.Exists(_configurationFilePath)
                    ? RuntimeConfigurationIntegrityObservation.Unknown
                    : RuntimeConfigurationIntegrityObservation.Inactive;
            }

            RuntimeGenerationManifest manifest = JsonSerializer.Deserialize<RuntimeGenerationManifest>(
                File.ReadAllText(statePath))
                ?? throw new InvalidDataException("Runtime configuration generation state is empty.");
            RuntimeConfigurationGenerationState state = manifest.ToState();
            ValidateRuntimeGenerationState(state, manifest.SchemaVersion);
            if (!IsRuntimeGenerationConverged(state))
            {
                return RuntimeConfigurationIntegrityObservation.Unknown;
            }

            if (state.AppliedGeneration is null)
            {
                return File.Exists(_configurationFilePath)
                    ? RuntimeConfigurationIntegrityObservation.Unknown
                    : RuntimeConfigurationIntegrityObservation.Inactive;
            }

            if (!File.Exists(_configurationFilePath)
                || !StringComparer.Ordinal.Equals(
                    ComputeFileHash(_configurationFilePath),
                    state.AppliedContentHash))
            {
                return RuntimeConfigurationIntegrityObservation.Unknown;
            }

            RuntimeConfigurationActivationPlan observedPlan =
                MihomoYamlSemanticValidator.ReadActivationPlan(
                    File.ReadAllText(_configurationFilePath),
                    state.AppliedPlan!.ProfileId);
            if (!ActivationPlanMatchesConfiguration(state.AppliedPlan, observedPlan))
            {
                return RuntimeConfigurationIntegrityObservation.Unknown;
            }

            string snapshotPath = GetRuntimeSnapshotPath(
                state.AppliedGeneration.Value,
                state.AppliedContentHash!);
            if (!File.Exists(snapshotPath)
                || !StringComparer.Ordinal.Equals(
                    ComputeFileHash(snapshotPath),
                    state.AppliedContentHash))
            {
                return RuntimeConfigurationIntegrityObservation.Unknown;
            }

            return new RuntimeConfigurationIntegrityObservation(true, state.AppliedPlan);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            ArgumentException or
            CryptographicException or
            System.Security.SecurityException)
        {
            return RuntimeConfigurationIntegrityObservation.Unknown;
        }
        finally
        {
            _runtimeConfigurationGate.Release();
        }
    }

    /// <summary>
    /// Builds, stages, semantically validates, atomically promotes, applies, and readiness-verifies
    /// one runtime configuration generation, restoring the prior applied generation on failure.
    /// </summary>
    internal async Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        ICoreConfigurationRuntime runtime,
        CancellationToken cancellationToken)
    {
        return await ApplyRuntimeConfigurationAsync(
            _settings.ActiveProfileId,
            mode,
            transparentProxyEnabled,
            mixedPort,
            runtime,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies one desired profile without mutating the persisted active-profile pointer first.</summary>
    internal async Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(
        string profileId,
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        ICoreConfigurationRuntime runtime,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(runtime);
        await _runtimeConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingPath = null;
        try
        {
            Directory.CreateDirectory(_configurationDirectoryPath);
            RuntimeConfigurationGenerationState baseline =
                await LoadOrBootstrapRuntimeGenerationStateAsync(cancellationToken).ConfigureAwait(false);
            baseline = await ReconcileAppliedConfigurationAsync(
                baseline,
                runtime,
                cancellationToken).ConfigureAwait(false);

            string normalizedProfileId = NormalizeProfileId(profileId);
            string candidateText = BuildRuntimeConfiguration(
                normalizedProfileId,
                mixedPort,
                mode,
                transparentProxyEnabled);
            string candidateHash = ComputeContentHash(candidateText);
            RuntimeConfigurationActivationPlan desiredPlan = new(
                mode,
                transparentProxyEnabled,
                mixedPort,
                normalizedProfileId);
            long desiredGeneration = checked(Math.Max(
                baseline.DesiredGeneration,
                baseline.AppliedGeneration ?? 0) + 1);
            RuntimeConfigurationGenerationState desired = baseline with
            {
                DesiredGeneration = desiredGeneration,
                DesiredContentHash = candidateHash,
                DesiredPlan = desiredPlan,
            };
            await PersistRuntimeGenerationStateAsync(desired, cancellationToken).ConfigureAwait(false);

            stagingPath = Path.Combine(
                _configurationDirectoryPath,
                $"config.yaml.runtime-staging.{Guid.NewGuid():N}");
            await WriteDurableTextAsync(stagingPath, candidateText, cancellationToken).ConfigureAwait(false);

            try
            {
                MihomoYamlSemanticValidator.ValidateManagedRuntimeConfiguration(
                    candidateText,
                    mixedPort,
                    mode,
                    transparentProxyEnabled,
                    _settings.MihomoControllerSecret);
                await _validator
                    .ValidateAsync(_configurationDirectoryPath, stagingPath, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException validationCancellationFailure)
            {
                DeleteFileIfPresent(stagingPath);
                stagingPath = null;
                Exception? stateRollbackFailure = await TryPersistRuntimeGenerationStateAsync(baseline)
                    .ConfigureAwait(false);
                if (stateRollbackFailure is not null)
                {
                    throw new AggregateException(
                        "Runtime configuration validation was cancelled and its desired generation could not be rolled back.",
                        validationCancellationFailure,
                        stateRollbackFailure);
                }

                throw;
            }
            catch (Exception validationFailure)
            {
                DeleteFileIfPresent(stagingPath);
                stagingPath = null;
                Exception? stateRollbackFailure = await TryPersistRuntimeGenerationStateAsync(baseline)
                    .ConfigureAwait(false);
                return new RuntimeConfigurationTransactionResult(
                    stateRollbackFailure is null
                        ? RuntimeConfigurationTransactionOutcome.Rejected
                        : RuntimeConfigurationTransactionOutcome.RollbackFailed,
                    desired,
                    GetState(),
                    PreserveRuntimeDiagnostic(
                        validationFailure,
                        RuntimeFailureDiagnostics.ConfigurationRejected),
                    stateRollbackFailure);
            }

            File.Move(stagingPath, _configurationFilePath, overwrite: true);
            stagingPath = null;

            Exception? activationFailure = null;
            try
            {
                CoreConfigurationState promotedConfiguration = GetState();
                await runtime
                    .ApplyAsync(promotedConfiguration, desiredGeneration, desiredPlan, cancellationToken)
                    .ConfigureAwait(false);
                if (!await runtime
                        .WaitUntilReadyAsync(
                            desiredGeneration,
                            candidateHash,
                            desiredPlan,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw new StableRuntimeDiagnosticException(
                        desiredPlan.TunEnabled
                            ? "service.controller.not_ready"
                            : RuntimeFailureDiagnostics.ControllerUnavailable,
                        "Mihomo did not confirm controller readiness for the desired configuration generation.");
                }

                await runtime
                    .CommitAsync(desiredGeneration, desiredPlan, cancellationToken)
                    .ConfigureAwait(false);
                await EnsureRuntimeSnapshotAsync(
                    desiredGeneration,
                    candidateHash,
                    candidateText,
                    desiredPlan,
                    cancellationToken).ConfigureAwait(false);
                RuntimeConfigurationGenerationState applied = desired with
                {
                    AppliedGeneration = desiredGeneration,
                    AppliedContentHash = candidateHash,
                    AppliedPlan = desiredPlan,
                };
                await PersistRuntimeGenerationStateAsync(applied, cancellationToken).ConfigureAwait(false);
                Exception? maintenanceFailure = TryCleanupRuntimeSnapshots(applied);
                return new RuntimeConfigurationTransactionResult(
                    RuntimeConfigurationTransactionOutcome.Applied,
                    applied,
                    promotedConfiguration,
                    Failure: null,
                    RollbackFailure: null)
                {
                    MaintenanceFailure = maintenanceFailure,
                };
            }
            catch (Exception exception)
            {
                activationFailure = exception;
            }

            Exception? rollbackFailure = await TryRestoreAppliedGenerationAsync(
                baseline,
                runtime).ConfigureAwait(false);
            if (activationFailure is OperationCanceledException cancellationFailure)
            {
                if (rollbackFailure is not null)
                {
                    throw new AggregateException(
                        "Runtime configuration was cancelled and its previous applied generation could not be restored.",
                        cancellationFailure,
                        rollbackFailure);
                }

                ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
            }

            RuntimeConfigurationTransactionOutcome outcome = rollbackFailure is null
                ? RuntimeConfigurationTransactionOutcome.RolledBack
                : RuntimeConfigurationTransactionOutcome.RollbackFailed;
            return new RuntimeConfigurationTransactionResult(
                outcome,
                desired,
                GetState(),
                activationFailure,
                rollbackFailure);
        }
        finally
        {
            if (stagingPath is not null)
            {
                DeleteFileIfPresent(stagingPath);
            }

            _runtimeConfigurationGate.Release();
        }
    }

    private async Task<RuntimeConfigurationGenerationState> LoadOrBootstrapRuntimeGenerationStateAsync(
        CancellationToken cancellationToken)
    {
        string statePath = GetRuntimeGenerationStatePath();
        if (File.Exists(statePath))
        {
            string json = await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false);
            RuntimeGenerationManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<RuntimeGenerationManifest>(json)
                    ?? throw new InvalidDataException("Runtime configuration generation state is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Runtime configuration generation state is invalid.", exception);
            }

            RuntimeConfigurationGenerationState loaded = manifest.ToState();
            ValidateRuntimeGenerationState(loaded, manifest.SchemaVersion);
            return loaded;
        }

        RuntimeConfigurationGenerationState initial;
        if (File.Exists(_configurationFilePath))
        {
            string existingText = await File
                .ReadAllTextAsync(_configurationFilePath, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                string existingHash = ComputeContentHash(existingText);
                RuntimeConfigurationActivationPlan initialPlan =
                    MihomoYamlSemanticValidator.ReadActivationPlan(existingText, _settings.ActiveProfileId);
                initial = new RuntimeConfigurationGenerationState(
                    0,
                    existingHash,
                    initialPlan,
                    null,
                    null,
                    null);
            }
            catch (ArgumentException)
            {
                // Legacy bytes have never passed this transaction's exact-candidate validation.
                // Treat malformed residue as untrusted rather than inferring an applied owner plan.
                initial = new RuntimeConfigurationGenerationState(0, null, null, null, null, null);
            }
        }
        else
        {
            initial = new RuntimeConfigurationGenerationState(0, null, null, null, null, null);
        }

        await PersistRuntimeGenerationStateAsync(initial, cancellationToken).ConfigureAwait(false);
        return initial;
    }

    private async Task<RuntimeConfigurationGenerationState> ReconcileAppliedConfigurationAsync(
        RuntimeConfigurationGenerationState state,
        ICoreConfigurationRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (state.AppliedGeneration is null)
        {
            await runtime.DeactivateAsync(cancellationToken).ConfigureAwait(false);
            DeleteFileIfPresent(_configurationFilePath);
            RuntimeConfigurationGenerationState inactive = CreateConvergedAppliedState(state);
            if (!RuntimeGenerationStatesEqual(state, inactive))
            {
                await PersistRuntimeGenerationStateAsync(inactive, cancellationToken).ConfigureAwait(false);
            }

            DeleteUnpublishedRuntimeSnapshots(appliedGeneration: null);
            return inactive;
        }

        string snapshotPath = GetRuntimeSnapshotPath(
            state.AppliedGeneration.Value,
            state.AppliedContentHash!);
        await VerifyRuntimeSnapshotAsync(
            snapshotPath,
            state.AppliedContentHash!,
            state.AppliedPlan!,
            cancellationToken)
            .ConfigureAwait(false);
        bool configurationMatchesApplied = false;
        if (File.Exists(_configurationFilePath))
        {
            string currentHash = await ComputeFileHashAsync(_configurationFilePath, cancellationToken)
                .ConfigureAwait(false);
            configurationMatchesApplied = StringComparer.Ordinal.Equals(
                currentHash,
                state.AppliedContentHash);
        }

        bool manifestConverged = IsRuntimeGenerationConverged(state);
        if (manifestConverged && configurationMatchesApplied)
        {
            DeleteUnpublishedRuntimeSnapshots(state.AppliedGeneration);
            return state;
        }

        if (manifestConverged)
        {
            // Make the recovery phase durable before the file begins to look
            // converged. A crash between file restore and runtime readiness must
            // remain observable as degraded on the next process start.
            RuntimeConfigurationGenerationState recoveryMarker = state with
            {
                DesiredGeneration = checked(state.AppliedGeneration.Value + 1),
                DesiredContentHash = state.AppliedContentHash,
                DesiredPlan = state.AppliedPlan,
            };
            await PersistRuntimeGenerationStateAsync(recoveryMarker, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!configurationMatchesApplied)
        {
            await RestoreSnapshotFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        }

        await runtime
            .ApplyAsync(
                GetState(),
                state.AppliedGeneration.Value,
                state.AppliedPlan!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await runtime
                .WaitUntilReadyAsync(
                    state.AppliedGeneration.Value,
                    state.AppliedContentHash!,
                    state.AppliedPlan!,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            throw new StableRuntimeDiagnosticException(
                state.AppliedPlan!.TunEnabled
                    ? "service.controller.not_ready"
                    : RuntimeFailureDiagnostics.ControllerUnavailable,
                "Mihomo did not confirm controller readiness while reconciling the applied runtime generation.");
        }

        await runtime
            .CommitAsync(
                state.AppliedGeneration.Value,
                state.AppliedPlan!,
                cancellationToken)
            .ConfigureAwait(false);
        RuntimeConfigurationGenerationState reconciled = CreateConvergedAppliedState(state);
        await PersistRuntimeGenerationStateAsync(reconciled, cancellationToken).ConfigureAwait(false);
        DeleteUnpublishedRuntimeSnapshots(reconciled.AppliedGeneration);
        return reconciled;
    }

    private async Task<Exception?> TryRestoreAppliedGenerationAsync(
        RuntimeConfigurationGenerationState baseline,
        ICoreConfigurationRuntime runtime)
    {
        try
        {
            if (baseline.AppliedGeneration is null)
            {
                await runtime.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
                DeleteFileIfPresent(_configurationFilePath);
                await PersistRuntimeGenerationStateAsync(
                    CreateConvergedAppliedState(baseline),
                    CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            string snapshotPath = GetRuntimeSnapshotPath(
                baseline.AppliedGeneration.Value,
                baseline.AppliedContentHash!);
            await VerifyRuntimeSnapshotAsync(
                snapshotPath,
                baseline.AppliedContentHash!,
                baseline.AppliedPlan!,
                CancellationToken.None).ConfigureAwait(false);
            await RestoreSnapshotFileAsync(snapshotPath, CancellationToken.None).ConfigureAwait(false);
            await runtime
                .ApplyAsync(
                    GetState(),
                    baseline.AppliedGeneration.Value,
                    baseline.AppliedPlan!,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!await runtime
                    .WaitUntilReadyAsync(
                        baseline.AppliedGeneration.Value,
                        baseline.AppliedContentHash!,
                        baseline.AppliedPlan!,
                        CancellationToken.None)
                    .ConfigureAwait(false))
            {
                throw new StableRuntimeDiagnosticException(
                    baseline.AppliedPlan!.TunEnabled
                        ? "service.controller.not_ready"
                        : RuntimeFailureDiagnostics.ControllerUnavailable,
                    "Mihomo did not confirm controller readiness after runtime configuration rollback.");
            }

            await runtime
                .CommitAsync(
                    baseline.AppliedGeneration.Value,
                    baseline.AppliedPlan!,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await PersistRuntimeGenerationStateAsync(
                CreateConvergedAppliedState(baseline),
                CancellationToken.None).ConfigureAwait(false);

            return null;
        }
        catch (Exception rollbackFailure)
        {
            return rollbackFailure;
        }
    }

    private async Task<Exception?> TryPersistRuntimeGenerationStateAsync(
        RuntimeConfigurationGenerationState state)
    {
        try
        {
            await PersistRuntimeGenerationStateAsync(state, CancellationToken.None)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception persistenceFailure)
        {
            return persistenceFailure;
        }
    }

    private static RuntimeConfigurationGenerationState CreateConvergedAppliedState(
        RuntimeConfigurationGenerationState state)
    {
        if (state.AppliedGeneration is null)
        {
            return new RuntimeConfigurationGenerationState(0, null, null, null, null, null);
        }

        return state with
        {
            DesiredGeneration = state.AppliedGeneration.Value,
            DesiredContentHash = state.AppliedContentHash,
            DesiredPlan = state.AppliedPlan,
        };
    }

    private static Exception PreserveRuntimeDiagnostic(Exception failure, string fallbackCode)
    {
        return RuntimeFailureDiagnostics.TryExtractCode(failure, out _)
            ? failure
            : new StableRuntimeDiagnosticException(
                fallbackCode,
                "Runtime configuration validation failed.",
                failure);
    }

    private static bool IsRuntimeGenerationConverged(RuntimeConfigurationGenerationState state)
    {
        if (state.AppliedGeneration is null)
        {
            return state.DesiredGeneration == 0
                && state.DesiredContentHash is null
                && state.DesiredPlan is null;
        }

        return state.DesiredGeneration == state.AppliedGeneration.Value
            && StringComparer.Ordinal.Equals(
                state.DesiredContentHash,
                state.AppliedContentHash)
            && state.DesiredPlan == state.AppliedPlan;
    }

    private static bool RuntimeGenerationStatesEqual(
        RuntimeConfigurationGenerationState left,
        RuntimeConfigurationGenerationState right)
    {
        return left == right;
    }

    private static bool ActivationPlanMatchesConfiguration(
        RuntimeConfigurationActivationPlan expected,
        RuntimeConfigurationActivationPlan observed)
    {
        bool modeMatches = expected.Mode == observed.Mode
            || expected.Mode == ClashSharpMode.Disabled
                && observed.Mode == ClashSharpMode.Standby;
        return modeMatches
            && expected.TunEnabled == observed.TunEnabled
            && expected.MixedPort == observed.MixedPort
            && StringComparer.Ordinal.Equals(expected.ProfileId, observed.ProfileId);
    }

    private async Task EnsureRuntimeSnapshotAsync(
        long generation,
        string contentHash,
        string configurationText,
        RuntimeConfigurationActivationPlan plan,
        CancellationToken cancellationToken)
    {
        string snapshotPath = GetRuntimeSnapshotPath(generation, contentHash);
        if (File.Exists(snapshotPath))
        {
            await VerifyRuntimeSnapshotAsync(snapshotPath, contentHash, plan, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        string temporaryPath = snapshotPath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            await WriteDurableTextAsync(temporaryPath, configurationText, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, snapshotPath, overwrite: false);
        }
        finally
        {
            DeleteFileIfPresent(temporaryPath);
        }
    }

    private Exception? TryCleanupRuntimeSnapshots(RuntimeConfigurationGenerationState applied)
    {
        try
        {
            string snapshotsDirectory = GetRuntimeSnapshotsDirectoryPath();
            if (!Directory.Exists(snapshotsDirectory))
            {
                return null;
            }

            string appliedPath = GetRuntimeSnapshotPath(
                applied.AppliedGeneration!.Value,
                applied.AppliedContentHash!);
            string[] retained = Directory
                .EnumerateFiles(snapshotsDirectory, "*.yaml", SearchOption.TopDirectoryOnly)
                .OrderByDescending(ParseRuntimeSnapshotGeneration)
                .Take(RuntimeSnapshotRetentionCount)
                .Append(appliedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            HashSet<string> retainedPaths = new(retained, StringComparer.OrdinalIgnoreCase);
            foreach (string snapshotPath in Directory.EnumerateFiles(
                snapshotsDirectory,
                "*.yaml",
                SearchOption.TopDirectoryOnly))
            {
                if (!retainedPaths.Contains(snapshotPath))
                {
                    File.Delete(snapshotPath);
                }
            }

            return null;
        }
        catch (Exception maintenanceFailure)
        {
            return maintenanceFailure;
        }
    }

    private void DeleteUnpublishedRuntimeSnapshots(long? appliedGeneration)
    {
        string snapshotsDirectory = GetRuntimeSnapshotsDirectoryPath();
        if (!Directory.Exists(snapshotsDirectory))
        {
            return;
        }

        foreach (string snapshotPath in Directory.EnumerateFiles(
            snapshotsDirectory,
            "*.yaml",
            SearchOption.TopDirectoryOnly))
        {
            if (appliedGeneration is null
                || ParseRuntimeSnapshotGeneration(snapshotPath) > appliedGeneration.Value)
            {
                File.Delete(snapshotPath);
            }
        }
    }

    private static long ParseRuntimeSnapshotGeneration(string snapshotPath)
    {
        string fileName = Path.GetFileName(snapshotPath);
        int separatorIndex = fileName.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex <= 0
            || !long.TryParse(
                fileName.AsSpan(0, separatorIndex),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long generation))
        {
            throw new InvalidDataException("Runtime configuration snapshot name is invalid.");
        }

        return generation;
    }

    private static async Task VerifyRuntimeSnapshotAsync(
        string snapshotPath,
        string expectedHash,
        RuntimeConfigurationActivationPlan expectedPlan,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(snapshotPath))
        {
            throw new InvalidDataException("The applied runtime configuration snapshot is missing.");
        }

        string actualHash = await ComputeFileHashAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(actualHash, expectedHash))
        {
            throw new InvalidDataException("The applied runtime configuration snapshot hash is invalid.");
        }

        string snapshotText = await File.ReadAllTextAsync(snapshotPath, cancellationToken)
            .ConfigureAwait(false);
        RuntimeConfigurationActivationPlan observedPlan =
            MihomoYamlSemanticValidator.ReadActivationPlan(snapshotText, expectedPlan.ProfileId);
        if (!ActivationPlanMatchesConfiguration(expectedPlan, observedPlan))
        {
            throw new InvalidDataException(
                "The applied runtime configuration snapshot does not match its activation plan.");
        }
    }

    private async Task RestoreSnapshotFileAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        string restorePath = _configurationFilePath + $".restore.{Guid.NewGuid():N}";
        try
        {
            await using (FileStream source = new(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = new(
                restorePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            File.Move(restorePath, _configurationFilePath, overwrite: true);
        }
        finally
        {
            DeleteFileIfPresent(restorePath);
        }
    }

    private async Task PersistRuntimeGenerationStateAsync(
        RuntimeConfigurationGenerationState state,
        CancellationToken cancellationToken)
    {
        ValidateRuntimeGenerationState(state, RuntimeGenerationStateSchemaVersion);
        string statePath = GetRuntimeGenerationStatePath();
        string temporaryPath = statePath + $".tmp.{Guid.NewGuid():N}";
        string json = JsonSerializer.Serialize(RuntimeGenerationManifest.FromState(state)) + "\n";
        try
        {
            await WriteDurableTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            DeleteFileIfPresent(temporaryPath);
        }
    }

    private static async Task WriteDurableTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private string GetRuntimeGenerationStatePath()
    {
        return Path.Combine(_configurationDirectoryPath, "config.runtime-state.json");
    }

    private string GetRuntimeSnapshotPath(long generation, string contentHash)
    {
        return Path.Combine(
            GetRuntimeSnapshotsDirectoryPath(),
            $"{generation:D19}-{contentHash}.yaml");
    }

    private string GetRuntimeSnapshotsDirectoryPath()
    {
        return Path.Combine(_configurationDirectoryPath, "runtime-generations");
    }

    private static string ComputeContentHash(string content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string ComputeFileHash(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void ValidateRuntimeGenerationState(
        RuntimeConfigurationGenerationState state,
        int schemaVersion)
    {
        if (schemaVersion != RuntimeGenerationStateSchemaVersion
            || state.DesiredGeneration < 0
            || state.AppliedGeneration is < 0
            || state.AppliedGeneration > state.DesiredGeneration
            || state.DesiredContentHash is not null && !IsCanonicalContentHash(state.DesiredContentHash)
            || state.DesiredGeneration > 0 && state.DesiredContentHash is null
            || (state.DesiredContentHash is null) != (state.DesiredPlan is null)
            || state.AppliedContentHash is not null && !IsCanonicalContentHash(state.AppliedContentHash)
            || (state.AppliedGeneration is null) != (state.AppliedContentHash is null)
            || (state.AppliedGeneration is null) != (state.AppliedPlan is null))
        {
            throw new InvalidDataException("Runtime configuration generation state violates its schema.");
        }

        ValidateActivationPlan(state.DesiredPlan);
        ValidateActivationPlan(state.AppliedPlan);
    }

    private static void ValidateActivationPlan(RuntimeConfigurationActivationPlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        if (plan.Mode is not ClashSharpMode.Disabled
                and not ClashSharpMode.Standby
                and not ClashSharpMode.RuleTakeover
                and not ClashSharpMode.FullTakeover
            || plan.MixedPort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(plan.ProfileId)
            || plan.TunEnabled
                && plan.Mode is not ClashSharpMode.RuleTakeover and not ClashSharpMode.FullTakeover)
        {
            throw new InvalidDataException("Runtime configuration activation plan is invalid.");
        }
    }

    private static bool IsCanonicalContentHash(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class RuntimeGenerationManifest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("desiredGeneration")]
        public long DesiredGeneration { get; init; }

        [JsonPropertyName("desiredContentHash")]
        public string? DesiredContentHash { get; init; }

        [JsonPropertyName("desiredPlan")]
        public RuntimeConfigurationActivationPlan? DesiredPlan { get; init; }

        [JsonPropertyName("appliedGeneration")]
        public long? AppliedGeneration { get; init; }

        [JsonPropertyName("appliedContentHash")]
        public string? AppliedContentHash { get; init; }

        [JsonPropertyName("appliedPlan")]
        public RuntimeConfigurationActivationPlan? AppliedPlan { get; init; }

        public RuntimeConfigurationGenerationState ToState()
        {
            return new RuntimeConfigurationGenerationState(
                DesiredGeneration,
                DesiredContentHash,
                DesiredPlan,
                AppliedGeneration,
                AppliedContentHash,
                AppliedPlan);
        }

        public static RuntimeGenerationManifest FromState(RuntimeConfigurationGenerationState state)
        {
            return new RuntimeGenerationManifest
            {
                SchemaVersion = RuntimeGenerationStateSchemaVersion,
                DesiredGeneration = state.DesiredGeneration,
                DesiredContentHash = state.DesiredContentHash,
                DesiredPlan = state.DesiredPlan,
                AppliedGeneration = state.AppliedGeneration,
                AppliedContentHash = state.AppliedContentHash,
                AppliedPlan = state.AppliedPlan,
            };
        }
    }
}

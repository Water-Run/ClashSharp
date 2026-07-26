using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Settings;

namespace ClashSharp.Infrastructure.Settings;

/// <summary>
/// Persists one canonical settings envelope beneath one immutable data generation.
/// </summary>
public sealed partial class JsonSettingsRepository : ISettingsRepository
{
    private const string SettingsDirectoryName = "Settings";
    private const string SchemaDirectoryName = "v1";
    private const string PrimaryFileName = "settings-envelope.json";
    private const string BackupFileName = "settings-envelope.backup.json";
    private readonly SettingsRegistry _registry;
    private readonly SettingsEnvelopeValidator _validator;
    private readonly ISettingsPersistenceFaultInjector _faultInjector;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <summary>Initializes a pure, generation-pinned settings repository.</summary>
    /// <param name="generation">Immutable owner of every repository path.</param>
    /// <param name="registry">Canonical settings schema registry.</param>
    /// <param name="faultInjector">Optional deterministic persistence-cut injector.</param>
    public JsonSettingsRepository(
        DataGenerationDescriptor generation,
        SettingsRegistry registry,
        ISettingsPersistenceFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(registry);

        Generation = generation;
        _registry = registry;
        _validator = new SettingsEnvelopeValidator(registry);
        _faultInjector =
            faultInjector ?? new NullSettingsPersistenceFaultInjector();
        SettingsDirectoryPath = Path.Combine(
            generation.RootPath,
            SettingsDirectoryName,
            SchemaDirectoryName);
        PrimaryPath = Path.Combine(SettingsDirectoryPath, PrimaryFileName);
        BackupPath = Path.Combine(SettingsDirectoryPath, BackupFileName);
        LockPath = Path.Combine(
            generation.RootPath,
            DataGenerationIdentityMarker.FileName);
    }

    /// <inheritdoc />
    public DataGenerationDescriptor Generation { get; }

    internal string SettingsDirectoryPath { get; }

    internal string PrimaryPath { get; }

    internal string BackupPath { get; }

    internal string LockPath { get; }

    /// <inheritdoc />
    public async Task<SettingsPersistenceResult> OpenAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLayout();
            await using FileStream repositoryLock =
                await AcquireRepositoryLockAsync(cancellationToken)
                    .ConfigureAwait(false);
            CleanupCandidates();
            return await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return UnavailableResult();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SettingsPersistenceResult> SaveAsync(
        SettingsEnvelope envelope,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        SettingsPersistenceResult? invalid =
            ValidateSaveRequest(envelope, expectedRevision);
        if (invalid is not null)
        {
            return invalid;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLayout();
            await using FileStream repositoryLock =
                await AcquireRepositoryLockAsync(cancellationToken)
                    .ConfigureAwait(false);
            CleanupCandidates();
            return await SaveCoreAsync(
                    envelope,
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return UnavailableResult();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private SettingsPersistenceResult? ValidateSaveRequest(
        SettingsEnvelope envelope,
        long expectedRevision)
    {
        SettingsEnvelopeValidationResult validation =
            _validator.Validate(envelope);
        if (!validation.IsValid)
        {
            SettingsEnvelopeValidationError first = validation.Errors[0];
            return SettingsPersistenceResult.Invalid(
                new SettingsPersistenceDiagnostic(first.Code, first.Path));
        }

        if (expectedRevision < 0 || expectedRevision == long.MaxValue)
        {
            return SettingsPersistenceResult.Invalid(
                new SettingsPersistenceDiagnostic(
                    "settings.persistence.revision_invalid",
                    "envelopeRevision"));
        }

        return null;
    }

    private SettingsPersistenceResult UnavailableResult() =>
        SettingsPersistenceResult.Unavailable(
            new SettingsPersistenceDiagnostic(
                "settings.persistence.unavailable",
                SettingsDirectoryPath));

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SettingsEnvelopeCodecException;
}

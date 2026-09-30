using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Owns the actual settings, profile, log, configuration and trigger repositories for one immutable data root.</summary>
internal sealed class AppDataGenerationRepositories : IServiceProvider, IAsyncDisposable
{
    private readonly object _disposalLock = new();
    private readonly ITriggerTrafficContextSource _trafficContext;
    private readonly List<IRuntimeParticipant> _producers = [];
    private readonly List<ISettingsApplicationParticipant> _participants = [];
    private Task? _disposal;
    private bool _closing;
    private int _stoppedProducers;
    private int _disposedParticipants;
    private SettingsGenerationContext? _settingsContext;
    private AppDataGenerationRuntime? _runtime;

    public AppDataGenerationRepositories(
        SettingsAuthoritySession session, ISettingsAuthority settingsAuthority,
        MutationAdmissionBarrier admission, FairAsyncMutationGate mutationGate,
        IControllerCredentialProvider credentials, ICoreConfigurationProfileMetrics metrics,
        ICoreConfigurationValidator validator, Func<string, string> getString,
        Func<CoreConfigurationService, SettingsAuthoritySession, IProfileCatalogRuntime> createProfileRuntime,
        Func<CoreConfigurationService, ProxySelectionService>? createProxySelections = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(settingsAuthority);
        ArgumentNullException.ThrowIfNull(createProfileRuntime);
        GenerationRepositorySettings settings = new(session, settingsAuthority);
        Configuration = new(Path.Combine(session.Generation.RootPath, "mihomo"), settings, credentials, metrics, validator, getString);
        Logs = LogStorageServiceFactory.CreateForDirectory(session.Generation.RootPath, () => settings.ActiveProfileId);
        _trafficContext = new SqliteTriggerTrafficContextSource(Logs.DatabasePath);
        Profiles = ProfileCatalogServiceFactory.CreateForDirectory(session.Generation.RootPath, settings,
            new ProfileCatalogCoreConfigurationAdapter(Configuration), createProfileRuntime(Configuration, session),
            new ProfileCatalogLogAdapter(Logs), getString, new ProfileCatalogMutationCoordinator(admission, mutationGate));
        Triggers = new(Path.Combine(session.Generation.RootPath, "Triggers.db"));
        ProxySelections = createProxySelections?.Invoke(Configuration);
    }

    public DataGenerationDescriptor Generation => Session.Generation;
    public SettingsAuthoritySession Session { get; }
    public CoreConfigurationService Configuration { get; }
    public LogStorageService Logs { get; }
    public ProfileCatalogService Profiles { get; }
    public SqliteTriggerRepository Triggers { get; }
    public ProxySelectionService? ProxySelections { get; }

    public void AttachRuntime(AppDataGenerationRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_disposalLock)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_runtime is not null || _settingsContext is not null || !ReferenceEquals(runtime.Repositories, this))
            {
                throw new InvalidOperationException("Runtime ownership does not match this unsealed generation.");
            }
            _runtime = runtime;
        }
    }

    public async Task OpenStorageAsync(bool allowCreate, MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        SettingsAuthorityResult settings = await Session.OpenAdmittedAsync(lease, cancellationToken).ConfigureAwait(false);
        if (!settings.IsSucceeded || settings.Envelope is null)
        {
            throw new InvalidDataException($"The generation settings are unavailable: {settings.Status} ({settings.Code}).");
        }
        if (!allowCreate)
        {
            RequireExistingDatabase(Logs.DatabasePath);
            RequireExistingDatabase(Path.Combine(Generation.RootPath, "Triggers.db"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        _ = Logs.GetStorageSummary();
        Profiles.OpenGenerationStorage(allowCreate);
        TriggerMigrationResult migration = await new TriggerMigrationCoordinator(Triggers, Path.Combine(Generation.RootPath, "Triggers.json"))
            .MigrateAsync(cancellationToken).ConfigureAwait(false);
        if (migration.Status is TriggerMigrationStatus.Unavailable or TriggerMigrationStatus.Quarantined)
        {
            throw new InvalidDataException($"The generation trigger migration is incomplete: {migration.Status}.");
        }
        TriggerPersistenceResult<TriggerRepositorySnapshot> triggers = await Triggers.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!triggers.IsSucceeded || triggers.Value is null) { throw new InvalidDataException("The generation trigger repository is unavailable."); }
    }

    /// <summary>Transfers a newly constructed producer to this generation before it can start.</summary>
    public T OwnProducer<T>(T producer) where T : class, IRuntimeParticipant
    {
        ArgumentNullException.ThrowIfNull(producer);
        lock (_disposalLock)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_settingsContext is not null) { throw new InvalidOperationException("Generation composition is already sealed."); }
            if (_producers.Any(existing => string.Equals(existing.Name, producer.Name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Generation producer names must be unique.");
            }
            _producers.Add(producer);
        }
        return producer;
    }

    public void OwnSettingsParticipant(ISettingsApplicationParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        lock (_disposalLock)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_settingsContext is not null || _participants.Any(existing => existing.ApplicationKind == participant.ApplicationKind))
            {
                throw new InvalidOperationException("Settings participant ownership is already assigned.");
            }
            _participants.Add(participant);
        }
    }

    public void SealComposition()
    {
        lock (_disposalLock)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            SettingApplicationKind[] expected = SettingsRegistry.Default.Definitions.Select(definition => definition.ApplicationKind).Distinct().Order().ToArray();
            if (_settingsContext is not null || !expected.SequenceEqual(_participants.Select(participant => participant.ApplicationKind).Order()))
            {
                throw new InvalidOperationException("A generation requires every registered settings participant exactly once.");
            }
            _settingsContext = new(Session, _participants);
        }
    }

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        lock (_disposalLock)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_settingsContext is null) { throw new InvalidOperationException("Generation composition has not been sealed."); }
            if (serviceType == typeof(SettingsGenerationContext)) { return _settingsContext; }
            if (serviceType == typeof(AppDataGenerationRuntime)) { return _runtime; }
            if (serviceType == typeof(SettingsAuthoritySession)) { return Session; }
            if (serviceType == typeof(ProfileCatalogService) || serviceType == typeof(IProfileCatalog)) { return Profiles; }
            if (serviceType == typeof(LogStorageService) || serviceType == typeof(ILogStorage)) { return Logs; }
            if (serviceType == typeof(ITriggerTrafficContextSource)) { return _trafficContext; }
            if (serviceType == typeof(CoreConfigurationService) || serviceType == typeof(ICoreConfigurationStore)) { return Configuration; }
            if (serviceType == typeof(IProxySelectionService)) { return ProxySelections; }
            if (serviceType == typeof(ITriggerRepository) || serviceType == typeof(SqliteTriggerRepository)) { return Triggers; }
            return _participants.SingleOrDefault(serviceType.IsInstanceOfType);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalLock)
        {
            _closing = true;
            if (_disposal is null || _disposal.IsFaulted || _disposal.IsCanceled) { _disposal = DisposeCoreAsync(); }
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // A producer that cannot stop retains its data dependencies for a later cleanup retry.
        while (_stoppedProducers < _producers.Count)
        {
            await _producers[_producers.Count - 1 - _stoppedProducers].StopAsync(CancellationToken.None).ConfigureAwait(false);
            ++_stoppedProducers;
        }
        // Accepted profile transactions can still commit settings and write compensation logs.
        await Profiles.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        while (_disposedParticipants < _participants.Count)
        {
            ISettingsApplicationParticipant participant = _participants[_participants.Count - 1 - _disposedParticipants];
            if (participant is IAsyncDisposable asynchronous) { await asynchronous.DisposeAsync().ConfigureAwait(false); }
            else if (participant is IDisposable synchronous) { synchronous.Dispose(); }
            ++_disposedParticipants;
        }
        await Logs.DisposeAsync().ConfigureAwait(false);
    }

    private static void RequireExistingDatabase(string path)
    {
        // File.Exists hides access failures and must not authorize creating an empty published database.
        using FileStream verified = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        verified.ReadExactly(header);
        if (!header.SequenceEqual("SQLite format 3\0"u8)) { throw new InvalidDataException("The published database header is invalid."); }
    }
}

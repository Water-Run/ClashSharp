using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Applies complete appearance batches on the owning UI thread and independently observes their results.</summary>
public sealed class AppearanceSettingsParticipant : ISettingsApplicationParticipant, IAppearanceSettingsReader, IAsyncDisposable
{
    private static readonly HashSet<SettingKey> NativeKeys =
    [SettingsRegistry.Keys.DisplayLanguage, SettingsRegistry.Keys.AppThemeMode,
        SettingsRegistry.Keys.AppAccentColorMode, SettingsRegistry.Keys.AppAccentColorValue];
    private readonly object _lifetimeGate = new();
    private readonly DataGenerationDescriptor _generation;
    private readonly MutationAdmissionBarrier _admission;
    private readonly OwnedUiDispatcher _dispatcher;
    private readonly IAppearanceNativeSettings _native;
    private readonly IReadOnlyDictionary<SettingKey, SettingDefinition> _definitions;
    private AppearanceSettingsSnapshot? _installed;
    private int _retiring;
    private Task? _retirement;

    /// <summary>Creates a pure configuration owner; the caller transfers this generation's dispatcher lifetime.</summary>
    /// <param name="generation">The exact generation that owns the installed policies.</param>
    /// <param name="admission">Process-wide admission retained by the settings authority.</param>
    /// <param name="registry">The same canonical registry used by the settings session.</param>
    /// <param name="dispatcher">A dedicated operation owner bound to the live window.</param>
    /// <param name="native">The actual UI configuration boundary.</param>
    public AppearanceSettingsParticipant(DataGenerationDescriptor generation, MutationAdmissionBarrier admission,
        SettingsRegistry registry, OwnedUiDispatcher dispatcher, IAppearanceNativeSettings native)
    {
        _generation = generation ?? throw new ArgumentNullException(nameof(generation));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        ArgumentNullException.ThrowIfNull(registry);
        _definitions = registry.Definitions.Where(definition => definition.ApplicationKind == SettingApplicationKind.Appearance)
            .ToDictionary(definition => definition.Key);
        if (_definitions.Count <= NativeKeys.Count || NativeKeys.Any(key => !_definitions.TryGetValue(key, out SettingDefinition? definition)
                || definition.ValueType != SettingsRegistry.Default.Get(key.Value).ValueType)
            || _definitions.Values.Any(definition => definition.Authority != SettingAuthority.Internal))
        {
            throw new ArgumentException("Appearance requires canonical UI definitions and application-owned consumer policies.", nameof(registry));
        }
        _installed = new(_generation, _definitions.Where(pair => !NativeKeys.Contains(pair.Key))
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value.DefaultValue)));
    }

    /// <inheritdoc />
    public SettingApplicationKind ApplicationKind => SettingApplicationKind.Appearance;
    /// <inheritdoc />
    public AppearanceSettingsSnapshot CaptureSnapshot() => Volatile.Read(ref _installed)
        ?? throw new ObjectDisposedException(nameof(AppearanceSettingsParticipant));

    /// <inheritdoc />
    public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        Validate(request, admissionLease);
        return _dispatcher.InvokeAsync(() =>
        {
            Validate(request, admissionLease);
            AppearanceNativeConfiguration native = _native.CaptureConfiguration();
            AppearanceSettingsSnapshot installed = CaptureSnapshot();
            return new SettingsApplicationObservation(_generation, request.Batch.BatchId, request.Batch.AttemptId,
                request.Values.Keys.Select(key => new SettingValueChange(key, Read(key, native, installed))));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        Validate(request, admissionLease);
        return _dispatcher.InvokeAsync(() =>
        {
            Validate(request, admissionLease);
            AppearanceNativeConfiguration before = _native.CaptureConfiguration();
            AppearanceSettingsSnapshot installed = CaptureSnapshot();
            T Target<T>(SettingKey key) where T : notnull => request.Values.TryGetValue(key, out SettingValue? value)
                ? value.Get<T>() : Read(key, before, installed).Get<T>();
            AppearanceNativeConfiguration target = new(Target<AppLanguage>(SettingsRegistry.Keys.DisplayLanguage),
                Target<AppThemeMode>(SettingsRegistry.Keys.AppThemeMode),
                new(Target<AppAccentColorMode>(SettingsRegistry.Keys.AppAccentColorMode), Target<string>(SettingsRegistry.Keys.AppAccentColorValue)));
            Dictionary<SettingKey, SettingValue> policies = installed.Values.ToDictionary();
            foreach ((SettingKey key, SettingValue value) in request.Values.Where(pair => !NativeKeys.Contains(pair.Key))) { policies[key] = value; }
            AppearanceSettingsSnapshot next = new(_generation, policies);
            ApplyNative(before, target);
            Volatile.Write(ref _installed, next);
            return true;
        }, cancellationToken);
    }

    /// <summary>Retires queued work and drains started UI callbacks before withdrawing the consumer snapshot.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate) { return new(_retirement ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        Volatile.Write(ref _retiring, 1);
        try { await _dispatcher.DisposeAsync().ConfigureAwait(false); }
        finally { Volatile.Write(ref _installed, null); }
    }

    private void ApplyNative(AppearanceNativeConfiguration before, AppearanceNativeConfiguration target)
    {
        try
        {
            ApplyNativeDifference(before, target);
            if (_native.CaptureConfiguration() != target) { throw new InvalidOperationException("The requested appearance could not be verified."); }
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            if (TryObserve(target)) { return; }
            if (TryObserve(before)) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
            List<Exception> failures = [failure];
            // These effects only replace this application's UI configuration. Recover the
            // observed baseline so an explicit retry can start from independently known state.
            Restore(() => { if (before.Language != target.Language) { _native.ApplyLanguage(before.Language); } }, failures);
            Restore(() => { if (before.Theme != target.Theme) { _native.ApplyTheme(before.Theme); } }, failures);
            Restore(() => { if (before.Accent != target.Accent) { _native.ApplyAccent(before.Accent); } }, failures);
            if (TryObserve(before)) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
            failures.Add(new InvalidOperationException("The previous appearance could not be independently restored."));
            throw new AggregateException("Appearance application and recovery did not reach a verified configuration.", failures);
        }
    }

    private void ApplyNativeDifference(AppearanceNativeConfiguration before, AppearanceNativeConfiguration target)
    {
        if (before.Accent != target.Accent) { _native.ApplyAccent(target.Accent); }
        if (before.Theme != target.Theme) { _native.ApplyTheme(target.Theme); }
        if (before.Language != target.Language) { _native.ApplyLanguage(target.Language); }
    }

    private bool TryObserve(AppearanceNativeConfiguration expected)
    {
        try { return _native.CaptureConfiguration() == expected; }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)) { return false; }
    }

    private static void Restore(Action action, ICollection<Exception> failures)
    {
        try { action(); }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)) { failures.Add(exception); }
    }

    private SettingValue Read(SettingKey key, AppearanceNativeConfiguration native, AppearanceSettingsSnapshot installed)
    {
        if (!NativeKeys.Contains(key)) { return installed.Values[key]; }
        object value = key == SettingsRegistry.Keys.DisplayLanguage ? native.Language
            : key == SettingsRegistry.Keys.AppThemeMode ? native.Theme
            : key == SettingsRegistry.Keys.AppAccentColorMode ? native.Accent.Mode : native.Accent.ColorValue;
        SettingNormalizationResult normalized = _definitions[key].NormalizeValue(value);
        return normalized.IsSuccess ? normalized.Value!
            : throw new InvalidOperationException("The UI returned a noncanonical appearance value.");
    }

    private void Validate(SettingsApplicationRequest request, MutationAdmissionLease lease)
    {
        ArgumentNullException.ThrowIfNull(request);
        _admission.EnsureActiveLease(lease);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _retiring) != 0, this);
        if (request.Phase == SettingsApplicationPhase.Startup) { _admission.EnsureActiveExclusiveLease(lease); }
        if (!_generation.IsSameGeneration(request.Generation) || request.Batch.ApplicationKind != ApplicationKind || request.Values.Count == 0
            || request.Values.Any(pair => !_definitions.TryGetValue(pair.Key, out SettingDefinition? definition)
                || !pair.Value.Equals(definition.Normalize(pair.Value.CanonicalText).Value)))
        {
            throw new InvalidOperationException("The appearance attempt does not match its generation, participant or canonical values.");
        }
    }
}

using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;
using Windows.Storage;

namespace ClashSharp.Infrastructure.Settings;

/// <summary>Reads only registered legacy preferences during exclusively owned packaged-app startup.</summary>
/// <remarks>This adapter never writes LocalSettings and never requests the internal controller credential.</remarks>
public sealed class WindowsLegacySettingsSource : ILegacySettingsSource
{
    private readonly SettingsRegistry _registry;

    /// <summary>Creates a read-only adapter without accessing Windows storage.</summary>
    /// <param name="registry">Canonical allowlist and legacy aliases.</param>
    public WindowsLegacySettingsSource(SettingsRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public Task<LegacySettingsSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = ApplicationData.Current.LocalSettings.Values;
        Dictionary<string, object?> snapshot = new(StringComparer.Ordinal);
        foreach (string key in _registry.Definitions.SelectMany(
                     definition => new[] { definition.Key.Value }.Concat(definition.Aliases.Select(alias => alias.Value))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (values.TryGetValue(key, out object? value)) { snapshot.Add(key, value); }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LegacySettingsSnapshot(_registry, snapshot));
    }
}

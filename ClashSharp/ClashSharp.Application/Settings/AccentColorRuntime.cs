using System.Collections.ObjectModel;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Records accent application only after independently observing the complete resource palette.</summary>
/// <remarks>All calls are made on the resource dictionary's owning UI thread. Construction performs no platform access.</remarks>
public sealed class AccentColorRuntime
{
    private readonly IAccentResourceStore _resources;
    private readonly IReadOnlyCollection<string> _ownedKeys;
    private readonly Func<AccentColorConfiguration, IReadOnlyDictionary<string, AccentResourceValue>> _buildPalette;
    private Palette _verified;
    private Palette? _attempted;

    /// <summary>Creates an owner with a declared default policy that still requires a real resource observation.</summary>
    /// <param name="resources">The actual application dictionary boundary.</param>
    /// <param name="ownedKeys">Every primary resource key this owner can change.</param>
    /// <param name="buildPalette">Pure complete-palette construction; follow-system selections produce no overrides.</param>
    public AccentColorRuntime(IAccentResourceStore resources, IEnumerable<string> ownedKeys,
        Func<AccentColorConfiguration, IReadOnlyDictionary<string, AccentResourceValue>> buildPalette)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        ArgumentNullException.ThrowIfNull(ownedKeys);
        string[] keys = ownedKeys.ToArray();
        if (keys.Length == 0 || keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            throw new ArgumentException("Accent resource keys must be nonempty and unique.", nameof(ownedKeys));
        }
        _ownedKeys = Array.AsReadOnly(keys);
        _buildPalette = buildPalette ?? throw new ArgumentNullException(nameof(buildPalette));
        _verified = Build(new(AppAccentColorMode.FollowSystem, "#FF0078D4"));
    }

    /// <summary>Reads configured state only when the actual complete resource set supports that state.</summary>
    /// <returns>The independently verified current selection.</returns>
    public AccentColorConfiguration CaptureConfiguration()
    {
        IReadOnlyDictionary<string, AccentResourceValue?> actual = _resources.CaptureLocalOverrides(_ownedKeys);
        if (_attempted is not null && Matches(_attempted, actual)) { _verified = _attempted; _attempted = null; }
        if (!Matches(_verified, actual)) { throw new InvalidOperationException("The application accent resources are not verified."); }
        return _verified.Configuration;
    }

    /// <summary>Writes a complete palette, then reads every owned override before acknowledging installation.</summary>
    /// <param name="configuration">The complete canonical selection.</param>
    public void Apply(AccentColorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Palette target = Build(configuration);
        // Confirm availability before the first effect. An attempted palette is only a candidate
        // for later independent observation, never evidence that a write succeeded.
        _ = _resources.CaptureLocalOverrides(_ownedKeys);
        _attempted = target;
        Exception? writeFailure = null;
        try
        {
            foreach (string key in _ownedKeys)
            {
                if (target.Values.TryGetValue(key, out AccentResourceValue value)) { _resources.WriteOverride(key, value); }
                else { _resources.RemoveOverride(key); }
            }
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)) { writeFailure = exception; }

        if (!Matches(target, _resources.CaptureLocalOverrides(_ownedKeys)))
        {
            if (writeFailure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writeFailure).Throw(); }
            throw new InvalidOperationException("The complete application accent palette could not be verified.");
        }
        _verified = target;
        _attempted = null;
    }

    private Palette Build(AccentColorConfiguration configuration)
    {
        Dictionary<string, AccentResourceValue> values = _buildPalette(configuration).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (values.Keys.Any(key => !_ownedKeys.Contains(key, StringComparer.Ordinal))
            || values.Count != (configuration.Mode == AppAccentColorMode.FollowSystem ? 0 : _ownedKeys.Count))
        {
            throw new InvalidOperationException("The accent palette does not cover the declared resource contract.");
        }
        return new(configuration, new ReadOnlyDictionary<string, AccentResourceValue>(values));
    }

    private static bool Matches(Palette target, IReadOnlyDictionary<string, AccentResourceValue?> actual) =>
        actual.Count == target.Values.Count && target.Values.All(pair => actual.TryGetValue(pair.Key, out AccentResourceValue? observed) && observed == pair.Value);

    private sealed record Palette(AccentColorConfiguration Configuration, IReadOnlyDictionary<string, AccentResourceValue> Values);
}

using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Contains the configured accent policy and canonical custom color, including the retained color while following Windows.</summary>
public sealed record AccentColorConfiguration
{
    /// <summary>Normalizes one complete accent selection without touching platform resources.</summary>
    /// <param name="mode">A declared accent policy.</param>
    /// <param name="colorValue">A valid custom ARGB color.</param>
    public AccentColorConfiguration(AppAccentColorMode mode, string colorValue)
    {
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        SettingNormalizationResult normalized = SettingsRegistry.Default.Get(SettingsRegistry.Keys.AppAccentColorValue.Value).Normalize(colorValue);
        if (!normalized.IsSuccess) { throw new ArgumentException("The accent color is invalid.", nameof(colorValue)); }
        Mode = mode;
        ColorValue = normalized.Value!.Get<string>();
    }

    /// <summary>Gets the installed accent policy.</summary>
    public AppAccentColorMode Mode { get; }

    /// <summary>Gets the normalized configured custom color.</summary>
    public string ColorValue { get; }
}

/// <summary>Describes one color or fully opaque solid-brush resource independently of WinUI objects.</summary>
/// <param name="Argb">The exact ARGB channels; color alpha is independent of brush opacity.</param>
/// <param name="IsBrush">Whether the resource must be a solid brush instead of a color value.</param>
public readonly record struct AccentResourceValue(uint Argb, bool IsBrush);

using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.ApplicationModel.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClashSharp.Service;

/// <summary>Keeps custom accent resources in normal theme dictionaries so Windows contrast themes retain their own color pairs.</summary>
internal sealed class WinUiAccentResourceStore : IAccentResourceStore
{
    public IReadOnlyDictionary<string, AccentResourceValue?> CaptureLocalOverrides(IReadOnlyCollection<string> ownedKeys)
    {
        ResourceDictionary resources = GetResources();
        HashSet<string> keys = ownedKeys.ToHashSet(StringComparer.Ordinal);
        // A primary override would shadow every theme, including HighContrast. Refuse to
        // acknowledge a palette while such a foreign override can bypass this boundary.
        if (resources.Any(entry => entry.Key is string key && keys.Contains(key)))
        {
            throw new InvalidOperationException("A primary resource overrides the theme-aware accent palette.");
        }
        Dictionary<string, AccentResourceValue?> dark = ReadLocal(GetTheme(resources, "Dark"), keys);
        Dictionary<string, AccentResourceValue?> light = ReadLocal(GetTheme(resources, "Light"), keys);
        Dictionary<string, AccentResourceValue?> snapshot = new(StringComparer.Ordinal);
        foreach (string key in dark.Keys.Union(light.Keys, StringComparer.Ordinal))
        {
            snapshot[key] = dark.TryGetValue(key, out AccentResourceValue? darkValue) && darkValue is { } first
                && light.TryGetValue(key, out AccentResourceValue? lightValue) && lightValue is { } second
                && first.IsBrush == second.IsBrush
                ? first with { LightArgb = first.Argb == second.Argb ? null : second.Argb }
                : null;
        }
        return snapshot;
    }

    public void WriteOverride(string key, AccentResourceValue value)
    {
        ResourceDictionary resources = GetResources();
        // An explicit HighContrast dictionary prevents a normal-theme fallback. It remains
        // empty of our overrides, allowing the built-in controls' system resources to resolve.
        _ = GetOrCreateTheme(resources, "HighContrast");
        GetOrCreateTheme(resources, "Dark")[key] = Materialize(value.Argb, value.IsBrush);
        GetOrCreateTheme(resources, "Light")[key] = Materialize(value.LightArgb ?? value.Argb, value.IsBrush);
    }

    public void RemoveOverride(string key)
    {
        ResourceDictionary resources = GetResources();
        GetTheme(resources, "Dark")?.Remove(key);
        GetTheme(resources, "Light")?.Remove(key);
    }

    private static Dictionary<string, AccentResourceValue?> ReadLocal(ResourceDictionary? resources, HashSet<string> keys)
    {
        Dictionary<string, AccentResourceValue?> result = new(StringComparer.Ordinal);
        if (resources is null) { return result; }
        // Enumeration reads local entries; indexed lookups can resolve inherited resources.
        foreach (KeyValuePair<object, object> entry in resources)
        {
            if (entry.Key is string key && keys.Contains(key))
            {
                result.Add(key, entry.Value switch
                {
                    Color color => new(ToArgb(color), false),
                    SolidColorBrush brush when brush.Opacity == 1d => new(ToArgb(brush.Color), true),
                    _ => null,
                });
            }
        }
        return result;
    }

    private static ResourceDictionary? GetTheme(ResourceDictionary resources, string theme)
    {
        if (!resources.ThemeDictionaries.TryGetValue(theme, out object? value)) { return null; }
        return value as ResourceDictionary ?? throw new InvalidOperationException("The accent theme dictionary is incompatible.");
    }

    private static ResourceDictionary GetOrCreateTheme(ResourceDictionary resources, string theme)
    {
        ResourceDictionary? dictionary = GetTheme(resources, theme);
        if (dictionary is not null) { return dictionary; }
        dictionary = new();
        resources.ThemeDictionaries[theme] = dictionary;
        return dictionary;
    }

    private static object Materialize(uint argb, bool brush)
    {
        Color color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        return brush ? new SolidColorBrush(color) : color;
    }

    internal static uint ToArgb(Color color) => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static ResourceDictionary GetResources()
    {
        ResourceDictionary resources = Application.Current?.Resources ?? throw new InvalidOperationException("The WinUI application resources are unavailable.");
        if (!resources.DispatcherQueue.HasThreadAccess) { throw new InvalidOperationException("Accent resources require the owning UI thread."); }
        return resources;
    }
}

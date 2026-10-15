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
    private ResourceDictionary? _applicationResources;
    private ResourceDictionary? _overrides;

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
        ResourceDictionary? overrides = GetOverrides(resources, create: false);
        if (overrides is null) { return new Dictionary<string, AccentResourceValue?>(StringComparer.Ordinal); }
        Dictionary<string, AccentResourceValue?> dark = ReadLocal(GetTheme(overrides, "Dark"), keys);
        Dictionary<string, AccentResourceValue?> light = ReadLocal(GetTheme(overrides, "Light"), keys);
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
        ResourceDictionary overrides = GetOverrides(resources, create: true)!;
        // An explicit HighContrast dictionary prevents a normal-theme fallback. It remains
        // empty of our overrides, allowing the built-in controls' system resources to resolve.
        _ = GetOrCreateTheme(overrides, "HighContrast");
        GetOrCreateTheme(overrides, "Dark")[key] = Materialize(value.Argb, value.IsBrush);
        GetOrCreateTheme(overrides, "Light")[key] = Materialize(value.LightArgb ?? value.Argb, value.IsBrush);
    }

    public void RemoveOverride(string key)
    {
        ResourceDictionary resources = GetResources();
        ResourceDictionary? overrides = GetOverrides(resources, create: false);
        if (overrides is null) { return; }
        GetTheme(overrides, "Dark")?.Remove(key);
        GetTheme(overrides, "Light")?.Remove(key);
    }

    private ResourceDictionary? GetOverrides(ResourceDictionary resources, bool create)
    {
        if (_overrides is not null)
        {
            if (!ReferenceEquals(resources, _applicationResources)
                || resources.MergedDictionaries.Count == 0
                || !ReferenceEquals(resources.MergedDictionaries[^1], _overrides))
            {
                throw new InvalidOperationException("The owned accent dictionary no longer has verified precedence.");
            }
            return _overrides;
        }
        if (!create) { return null; }

        // WinUI searches a dictionary's merged entries before its ThemeDictionaries. Putting
        // our themes directly on Application.Resources lets XamlControlsResources win first.
        // A last merged dictionary supplies our normal-theme values before the framework,
        // while its empty HighContrast dictionary deliberately falls through to the system.
        ResourceDictionary overrides = new();
        overrides.ThemeDictionaries["HighContrast"] = new ResourceDictionary();
        resources.MergedDictionaries.Add(overrides);
        _applicationResources = resources;
        _overrides = overrides;
        return overrides;
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

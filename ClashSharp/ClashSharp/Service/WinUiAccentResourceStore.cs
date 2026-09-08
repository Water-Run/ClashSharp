using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.ApplicationModel.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClashSharp.Service;

/// <summary>Reads and changes primary application accent overrides without confusing merged Windows resources with owned values.</summary>
internal sealed class WinUiAccentResourceStore : IAccentResourceStore
{
    public IReadOnlyDictionary<string, AccentResourceValue?> CaptureLocalOverrides(IReadOnlyCollection<string> ownedKeys)
    {
        ResourceDictionary resources = GetResources();
        HashSet<string> keys = ownedKeys.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, AccentResourceValue?> snapshot = new(StringComparer.Ordinal);
        // Enumeration identifies local entries. Indexed lookup and ContainsKey may resolve merged
        // resources, which must remain available when the application follows the system accent.
        foreach (KeyValuePair<object, object> entry in resources)
        {
            if (entry.Key is string key && keys.Contains(key))
            {
                snapshot.Add(key, entry.Value switch
                {
                    Color color => new(ToArgb(color), false),
                    SolidColorBrush brush when brush.Opacity == 1d => new(ToArgb(brush.Color), true),
                    _ => null,
                });
            }
        }
        return snapshot;
    }

    public void WriteOverride(string key, AccentResourceValue value)
    {
        ResourceDictionary resources = GetResources();
        Color color = Color.FromArgb((byte)(value.Argb >> 24), (byte)(value.Argb >> 16), (byte)(value.Argb >> 8), (byte)value.Argb);
        resources[key] = value.IsBrush ? new SolidColorBrush(color) : color;
    }

    public void RemoveOverride(string key) => GetResources().Remove(key);

    internal static uint ToArgb(Color color) => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static ResourceDictionary GetResources()
    {
        ResourceDictionary resources = Application.Current?.Resources ?? throw new InvalidOperationException("The WinUI application resources are unavailable.");
        if (!resources.DispatcherQueue.HasThreadAccess) { throw new InvalidOperationException("Accent resources require the owning UI thread."); }
        return resources;
    }
}

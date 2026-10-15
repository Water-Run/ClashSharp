using System;
using Windows.UI;

namespace ClashSharp.Service;

/// <summary>Chooses readable text for custom accent fills and normal-theme content surfaces.</summary>
internal static class AccentContrast
{
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    /// <summary>Retains white text when it meets normal-text contrast; otherwise chooses black.</summary>
    internal static Color ForegroundFor(Color fill, bool lightTheme)
    {
        // The picker emits opaque colors. Retained/imported alpha values are assessed after
        // compositing on the corresponding normal-theme content surface.
        Color surface = lightTheme ? White : Color.FromArgb(255, 32, 32, 32);
        Color visible = Blend(surface, fill, fill.A / 255d);
        return Contrast(White, visible) >= 4.5 ? White : Black;
    }

    /// <summary>Gives hover and press a visible variation while retaining the base fill's shared on-accent foreground.</summary>
    internal static Color InteractionFillFor(Color accent, bool lightTheme, double amount)
    {
        Color foreground = ForegroundFor(accent, lightTheme);
        Color candidate = Blend(accent, foreground, amount);
        Color surface = lightTheme ? White : Color.FromArgb(255, 32, 32, 32);
        if (Contrast(foreground, Blend(surface, candidate, accent.A / 255d)) < 4.5)
        {
            candidate = Blend(accent, foreground.Equals(White) ? Black : White, amount);
        }
        return Color.FromArgb(accent.A, candidate.R, candidate.G, candidate.B);
    }

    /// <summary>Preserves the accent hue where possible while giving text sufficient contrast on normal content surfaces.</summary>
    internal static Color TextFor(Color accent, bool lightTheme)
    {
        Color text = Color.FromArgb(255, accent.R, accent.G, accent.B);
        // Conservative normal-theme surfaces also cover the nearby card/popup shades.
        Color surface = lightTheme ? Color.FromArgb(255, 240, 240, 240) : Color.FromArgb(255, 51, 51, 51);
        if (Contrast(text, surface) >= 4.5) { return text; }
        Color endpoint = lightTheme ? Black : White;
        Color readable = endpoint;
        double low = 0, high = 1;
        for (int iteration = 0; iteration < 16; iteration++)
        {
            double amount = (low + high) / 2;
            Color candidate = Blend(text, endpoint, amount);
            if (Contrast(candidate, surface) >= 4.5) { readable = candidate; high = amount; }
            else { low = amount; }
        }
        return readable;
    }

    private static Color Blend(Color first, Color second, double amount)
    {
        static byte Channel(byte start, byte end, double value) => (byte)Math.Round(start + (end - start) * value);
        return Color.FromArgb(255, Channel(first.R, second.R, amount), Channel(first.G, second.G, amount), Channel(first.B, second.B, amount));
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                double channel = value / 255d;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        double left = Luminance(first), right = Luminance(second);
        return (Math.Max(left, right) + 0.05) / (Math.Min(left, right) + 0.05);
    }
}

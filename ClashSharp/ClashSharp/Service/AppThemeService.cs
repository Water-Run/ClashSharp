/*
 * Application Theme Service
 * Applies user-selected display style to the WinUI root element
 *
 * @author: WaterRun
 * @file: Service/AppThemeService.cs
 * @date: 2026-06-17
 */

using System;
using ClashSharp.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClashSharp.Service;

/// <summary>Applies the configured app display style to the active window.</summary>
internal static class AppThemeService
{
    /// <summary>Application resource keys overridden for custom accent colors.</summary>
    private static readonly string[] AccentColorResourceKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorLight3",
        "SystemAccentColorDark1",
        "SystemAccentColorDark2",
        "SystemAccentColorDark3",
    ];

    /// <summary>Application brush keys overridden for custom accent colors.</summary>
    private static readonly string[] AccentBrushResourceKeys =
    [
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentFillColorDisabledBrush",
        "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush",
        "AccentTextFillColorDisabledBrush",
        "AccentButtonBackground",
        "AccentButtonBackgroundPointerOver",
        "AccentButtonBackgroundPressed",
        "AccentButtonBackgroundDisabled",
        "AccentButtonForeground",
        "AccentButtonForegroundPointerOver",
        "AccentButtonForegroundPressed",
        "AccentButtonForegroundDisabled",
    ];

    /// <summary>Applies <paramref name="mode"/> to the main window root when available.</summary>
    public static void Apply(AppThemeMode mode)
    {
        if (App.MainWindow?.Content is not FrameworkElement root)
        {
            return;
        }

        Apply(root, mode);
    }

    /// <summary>Applies <paramref name="mode"/> to a specific root element.</summary>
    public static void Apply(FrameworkElement root, AppThemeMode mode)
    {
        root.RequestedTheme = mode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Applies the configured accent color behavior to application resources.</summary>
    /// <param name="mode">Accent color behavior.</param>
    /// <param name="colorValue">Custom accent color in #AARRGGBB format.</param>
    public static void ApplyAccentColor(AppAccentColorMode mode, string colorValue)
    {
        if (Application.Current is null)
        {
            return;
        }

        ResourceDictionary resources = Application.Current.Resources;
        if (mode == AppAccentColorMode.FollowSystem)
        {
            foreach (string key in AccentColorResourceKeys)
            {
                resources.Remove(key);
            }

            foreach (string key in AccentBrushResourceKeys)
            {
                resources.Remove(key);
            }

            return;
        }

        Color accentColor = ParseAccentColorOrDefault(colorValue);
        Color light1 = Blend(accentColor, Colors.White, 0.30);
        Color light2 = Blend(accentColor, Colors.White, 0.50);
        Color light3 = Blend(accentColor, Colors.White, 0.70);
        Color dark1 = Blend(accentColor, Colors.Black, 0.25);
        Color dark2 = Blend(accentColor, Colors.Black, 0.45);
        Color dark3 = Blend(accentColor, Colors.Black, 0.65);

        resources["SystemAccentColor"] = accentColor;
        resources["SystemAccentColorLight1"] = light1;
        resources["SystemAccentColorLight2"] = light2;
        resources["SystemAccentColorLight3"] = light3;
        resources["SystemAccentColorDark1"] = dark1;
        resources["SystemAccentColorDark2"] = dark2;
        resources["SystemAccentColorDark3"] = dark3;

        resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accentColor);
        resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(light1);
        resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(light2);
        resources["AccentFillColorDisabledBrush"] = new SolidColorBrush(Color.FromArgb(0x5C, accentColor.R, accentColor.G, accentColor.B));
        resources["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(Colors.White);
        resources["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(Colors.White);
        resources["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        resources["AccentTextFillColorDisabledBrush"] = new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
        resources["AccentButtonBackground"] = new SolidColorBrush(accentColor);
        resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(light1);
        resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(dark1);
        resources["AccentButtonBackgroundDisabled"] = new SolidColorBrush(Color.FromArgb(0x5C, accentColor.R, accentColor.G, accentColor.B));
        resources["AccentButtonForeground"] = new SolidColorBrush(Colors.White);
        resources["AccentButtonForegroundPointerOver"] = new SolidColorBrush(Colors.White);
        resources["AccentButtonForegroundPressed"] = new SolidColorBrush(Colors.White);
        resources["AccentButtonForegroundDisabled"] = new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>Parses an accent color value, returning Windows blue when parsing fails.</summary>
    /// <param name="value">Hex color text.</param>
    /// <returns>Parsed color or the default Windows accent blue.</returns>
    public static Color ParseAccentColorOrDefault(string value)
    {
        return TryParseAccentColor(value, out Color color)
            ? color
            : Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    }

    /// <summary>Formats a color for persistence as #AARRGGBB.</summary>
    /// <param name="color">Color to format.</param>
    /// <returns>Uppercase ARGB hex color.</returns>
    public static string FormatAccentColor(Color color)
    {
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>Attempts to parse a 6- or 8-digit accent color.</summary>
    /// <param name="value">Hex color text.</param>
    /// <param name="color">Parsed color when successful.</param>
    /// <returns>True when parsing succeeded.</returns>
    private static bool TryParseAccentColor(string value, out Color color)
    {
        color = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length == 6)
        {
            normalized = $"FF{normalized}";
        }

        if (normalized.Length != 8)
        {
            return false;
        }

        try
        {
            byte alpha = Convert.ToByte(normalized[0..2], 16);
            byte red = Convert.ToByte(normalized[2..4], 16);
            byte green = Convert.ToByte(normalized[4..6], 16);
            byte blue = Convert.ToByte(normalized[6..8], 16);
            color = Color.FromArgb(alpha, red, green, blue);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Blends <paramref name="source"/> toward <paramref name="target"/>.</summary>
    /// <param name="source">Starting color.</param>
    /// <param name="target">Target color.</param>
    /// <param name="amount">Blend amount in the range [0, 1].</param>
    /// <returns>Blended color.</returns>
    private static Color Blend(Color source, Color target, double amount)
    {
        static byte Channel(byte source, byte target, double amount)
        {
            return (byte)Math.Clamp(Math.Round(source + ((target - source) * amount)), byte.MinValue, byte.MaxValue);
        }

        return Color.FromArgb(
            source.A,
            Channel(source.R, target.R, amount),
            Channel(source.G, target.G, amount),
            Channel(source.B, target.B, amount));
    }
}

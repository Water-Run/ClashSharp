extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using AppThemeService = ClashSharpUi::ClashSharp.Service.AppThemeService;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Exercises the main assembly's real palette composition through its resource dictionary boundary.</summary>
public sealed class AppThemePaletteTests
{
    [Theory]
    [InlineData("#FFFFFFFF")]
    [InlineData("#FFFFFF00")]
    [InlineData("#FF00FFFF")]
    [InlineData("#FF808080")]
    [InlineData("#FF0078D4")]
    [InlineData("#FF000000")]
    [InlineData("#FFEF5350")]
    public void EnabledAccentContentRemainsReadableAcrossInteractionStates(string color)
    {
        ResourceStore resources = new();
        AppThemeService.CreateAccentRuntime(resources).Apply(new(AppAccentColorMode.Custom, color));
        (string Foreground, string Background)[] pairs =
        [
            ("AccentButtonForeground", "AccentButtonBackground"),
            ("AccentButtonForegroundPointerOver", "AccentButtonBackgroundPointerOver"),
            ("AccentButtonForegroundPressed", "AccentButtonBackgroundPressed"),
            ("ToggleButtonForegroundChecked", "ToggleButtonBackgroundChecked"),
            ("ToggleButtonForegroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPointerOver"),
            ("ToggleButtonForegroundCheckedPressed", "ToggleButtonBackgroundCheckedPressed"),
            ("TextOnAccentFillColorPrimaryBrush", "AccentFillColorDefaultBrush"),
            ("TextOnAccentFillColorPrimaryBrush", "AccentFillColorSecondaryBrush"),
            ("TextOnAccentFillColorPrimaryBrush", "AccentFillColorTertiaryBrush"),
        ];
        foreach ((string foreground, string background) in pairs)
        {
            foreach (bool light in new[] { false, true })
            {
                double ratio = Contrast(ForTheme(resources.Local[foreground], light), ForTheme(resources.Local[background], light));
                Assert.True(ratio >= 4.5, $"{color} {foreground} (light={light}) has contrast {ratio:F3}.");
            }
        }
    }

    [Theory]
    [InlineData("#FFFFFFFF")]
    [InlineData("#FFFFFF00")]
    [InlineData("#FF00FFFF")]
    [InlineData("#FF808080")]
    [InlineData("#FF0078D4")]
    [InlineData("#FF000000")]
    [InlineData("#FFEF5350")]
    [InlineData("#804477AA")]
    public void AccentTextRemainsReadableOnNormalThemeContentSurfaces(string color)
    {
        ResourceStore resources = new();
        AppThemeService.CreateAccentRuntime(resources).Apply(new(AppAccentColorMode.Custom, color));
        foreach (string key in new[] { "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush",
            "AccentTextFillColorTertiaryBrush", "SystemControlForegroundAccentBrush", "SystemControlHyperlinkTextBrush" })
        {
            foreach (bool light in new[] { false, true })
            {
                uint foreground = ForTheme(resources.Local[key], light);
                Assert.Equal(0xFF000000u, foreground & 0xFF000000u);
                foreach (uint background in light ? new[] { 0xFFFFFFFFu, 0xFFF3F3F3u, 0xFFF0F0F0u }
                    : new[] { 0xFF000000u, 0xFF202020u, 0xFF333333u })
                {
                    double ratio = Contrast(foreground, background);
                    Assert.True(ratio >= 4.5, $"{color} {key} on {background:X8} has contrast {ratio:F3}.");
                }
            }
        }
    }

    [Fact]
    public void AChangedLightThemeCannotBeAcknowledgedAsTheCompleteConfiguredPalette()
    {
        ResourceStore resources = new();
        AccentColorRuntime runtime = AppThemeService.CreateAccentRuntime(resources);
        AccentColorConfiguration custom = new(AppAccentColorMode.Custom, "#804477AA");
        runtime.Apply(custom);
        AccentResourceValue installed = resources.Local["AccentButtonForeground"];
        resources.Local["AccentButtonForeground"] = installed with { LightArgb = 0xFFFFFFFF };

        Assert.Throws<InvalidOperationException>(() => runtime.CaptureConfiguration());

        resources.Local["AccentButtonForeground"] = installed;
        Assert.Equal(custom, runtime.CaptureConfiguration());
    }

    private static uint ForTheme(AccentResourceValue value, bool light) => light ? value.LightArgb ?? value.Argb : value.Argb;

    private static double Contrast(uint first, uint second)
    {
        static double Luminance(uint value)
        {
            static double Channel(uint channel)
            {
                double linear = channel / 255d;
                return linear <= 0.04045 ? linear / 12.92 : Math.Pow((linear + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel((value >> 16) & 255)
                + 0.7152 * Channel((value >> 8) & 255) + 0.0722 * Channel(value & 255);
        }
        double left = Luminance(first), right = Luminance(second);
        return (Math.Max(left, right) + 0.05) / (Math.Min(left, right) + 0.05);
    }

    [Fact]
    public void CustomAccent_InstallsTheCompleteProductionPaletteWithIndependentArgbExpectations()
    {
        ResourceStore resources = new();
        AccentColorRuntime runtime = AppThemeService.CreateAccentRuntime(resources);
        Assert.Equal(0, resources.ReadCount);
        Assert.Equal(0, resources.WriteCount);
        AccentColorConfiguration custom = new(AppAccentColorMode.Custom, "#804477AA");

        runtime.Apply(custom);

        Assert.Equal(custom, runtime.CaptureConfiguration());
        Assert.Equal(48, resources.Local.Count);
        Assert.Equal(48, resources.WriteCount);
        Assert.Equal(7, resources.Local.Count(pair => !pair.Value.IsBrush));
        Assert.Equal(41, resources.Local.Count(pair => pair.Value.IsBrush));
        Assert.Equal(new(0x804477AA, false), resources.Local["SystemAccentColor"]);
        Assert.Equal(new(0x807CA0C4, false), resources.Local["SystemAccentColorLight1"]);
        Assert.Equal(new(0x80A2BBD4, false), resources.Local["SystemAccentColorLight2"]);
        Assert.Equal(new(0x80C7D6E6, false), resources.Local["SystemAccentColorLight3"]);
        Assert.Equal(new(0x80335980, false), resources.Local["SystemAccentColorDark1"]);
        Assert.Equal(new(0x8025415E, false), resources.Local["SystemAccentColorDark2"]);
        Assert.Equal(new(0x80182A3C, false), resources.Local["SystemAccentColorDark3"]);
        Assert.Equal(new(0x804477AA, true), resources.Local["AccentButtonBackground"]);
        Assert.Equal(new(0x805A87B4, true, 0x803C6996), resources.Local["AccentButtonBackgroundPointerOver"]);
        Assert.Equal(new(0x807198BE, true, 0x80345A81), resources.Local["AccentButtonBackgroundPressed"]);
        Assert.Equal(new(0x5C4477AA, true), resources.Local["AccentButtonBackgroundDisabled"]);
        Assert.Equal(new(0x00FFFFFF, true), resources.Local["AccentButtonBorderBrush"]);
        Assert.Equal(new(0xFFFFFFFF, true, 0xFF000000), resources.Local["AccentButtonForeground"]);
        Assert.Equal(new(0x5CFFFFFF, true, 0x5C000000), resources.Local["AccentButtonForegroundDisabled"]);
        Assert.Equal(new(0x807198BE, true, 0x80345A81), resources.Local["ToggleSwitchFillOnPressed"]);
        Assert.Equal(new(0xFFFFFFFF, true, 0xFF000000), resources.Local["TextOnAccentFillColorPrimaryBrush"]);
    }

    [Fact]
    public void FollowSystem_RemovesEveryProductionOverrideAndRetainsTheConfiguredCustomColor()
    {
        ResourceStore resources = new();
        AccentResourceValue unrelated = new(0xFF123456, true);
        AccentResourceValue systemAccent = new(0xFF00CC88, false);
        resources.Local.Add("UnrelatedResource", unrelated);
        resources.Merged.Add("SystemAccentColor", systemAccent);
        AccentColorRuntime runtime = AppThemeService.CreateAccentRuntime(resources);
        runtime.Apply(new(AppAccentColorMode.Custom, "#804477AA"));
        AccentColorConfiguration followSystem = new(AppAccentColorMode.FollowSystem, "#804477AA");

        runtime.Apply(followSystem);

        Assert.Equal(followSystem, runtime.CaptureConfiguration());
        Assert.Equal(new KeyValuePair<string, AccentResourceValue>("UnrelatedResource", unrelated), Assert.Single(resources.Local));
        Assert.Equal(new KeyValuePair<string, AccentResourceValue>("SystemAccentColor", systemAccent), Assert.Single(resources.Merged));
        Assert.Equal(48, resources.Removed.Count);
        Assert.Equal(48, resources.Removed.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("UnrelatedResource", resources.Removed);
    }

    private sealed class ResourceStore : IAccentResourceStore
    {
        public Dictionary<string, AccentResourceValue> Local { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AccentResourceValue> Merged { get; } = new(StringComparer.Ordinal);
        public List<string> Removed { get; } = [];
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }

        public IReadOnlyDictionary<string, AccentResourceValue?> CaptureLocalOverrides(IReadOnlyCollection<string> ownedKeys)
        {
            ReadCount++;
            return Local.Where(pair => ownedKeys.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => (AccentResourceValue?)pair.Value, StringComparer.Ordinal);
        }

        public void WriteOverride(string key, AccentResourceValue value) { WriteCount++; Local[key] = value; }
        public void RemoveOverride(string key) { Removed.Add(key); Local.Remove(key); }
    }
}

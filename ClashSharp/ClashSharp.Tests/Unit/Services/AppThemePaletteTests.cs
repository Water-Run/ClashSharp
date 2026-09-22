extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using AppThemeService = ClashSharpUi::ClashSharp.Service.AppThemeService;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Exercises the main assembly's real palette composition through its resource dictionary boundary.</summary>
public sealed class AppThemePaletteTests
{
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
        Assert.Equal(new(0x807CA0C4, true), resources.Local["AccentButtonBackgroundPointerOver"]);
        Assert.Equal(new(0x80335980, true), resources.Local["AccentButtonBackgroundPressed"]);
        Assert.Equal(new(0x5C4477AA, true), resources.Local["AccentButtonBackgroundDisabled"]);
        Assert.Equal(new(0x00FFFFFF, true), resources.Local["AccentButtonBorderBrush"]);
        Assert.Equal(new(0xFFFFFFFF, true), resources.Local["AccentButtonForeground"]);
        Assert.Equal(new(0x5CFFFFFF, true), resources.Local["AccentButtonForegroundDisabled"]);
        Assert.Equal(new(0xCCFFFFFF, true), resources.Local["AccentTextFillColorTertiaryBrush"]);
        Assert.Equal(new(0x80A2BBD4, true), resources.Local["ToggleSwitchFillOnPressed"]);
        Assert.Equal(new(0xFFFFFFFF, true), resources.Local["TextOnAccentFillColorPrimaryBrush"]);
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

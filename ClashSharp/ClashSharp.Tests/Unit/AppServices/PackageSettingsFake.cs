using System.Globalization;
using System.Reflection;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>An in-memory settings store standing in for the persisted one; "persistence" is the object itself.</summary>
internal sealed class PackageSettingsFake : IClashDataPackageSettings
{
    internal const string DefaultHeroLayout =
        "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic,Availability";

    private const string DefaultTileLayout =
        "core,upload-rate,download-rate,active-connections,transparent-proxy,latency,active-profile,current-mode";

    private static readonly PropertyInfo[] Properties = [.. typeof(IClashDataPackageSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .OrderBy(static property => property.Name, StringComparer.Ordinal)];

    public AppLanguage DisplayLanguage { get; set; } = AppLanguage.AutoDetect;
    public DataUnitStyle DataUnitStyle { get; set; } = DataUnitStyle.BinaryWithDecimalLabels;
    public RateUnit RateUnit { get; set; }
    public ClockFormat ClockFormat { get; set; }
    public AppThemeMode AppThemeMode { get; set; } = AppThemeMode.FollowSystem;
    public AppAccentColorMode AppAccentColorMode { get; set; } = AppAccentColorMode.FollowSystem;
    public string AppAccentColorValue { get; set; } = "#FF0078D4";
    public bool LaunchAtStartupEnabled { get; set; }
    public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;
    public string ActiveProfileId { get; set; } = "direct";
    public bool TransparentProxyEnabled { get; set; } = true;
    public int MixedPort { get; set; } = 10000;
    public bool ConnectionSamplingEnabled { get; set; } = true;
    public int ConnectionSamplingIntervalSeconds { get; set; } = 30;
    public bool RestoreProxyOnExit { get; set; } = true;
    public bool CheckStaleProxyOnStartup { get; set; } = true;
    public bool StartupConflictCheckEnabled { get; set; } = true;
    public StartupBehaviorMode StartupBehaviorMode { get; set; } = StartupBehaviorMode.LastSetting;
    public bool ShowStartupGuideOnStartup { get; set; } = true;
    public bool TriggersEnabled { get; set; } = true;
    public bool TriggerNotificationsEnabled { get; set; } = true;
    public CloseBehaviorMode CloseBehaviorMode { get; set; } = CloseBehaviorMode.MinimizeToTray;
    public bool TrayUseMonochromeInactiveIcon { get; set; } = true;
    public string TrayVisibleFeatureIds { get; set; } = "status,mode,pages,transparent-proxy,settings,safe-exit";
    public bool NotificationEnabled { get; set; } = true;
    public NotificationLevel NotificationLevel { get; set; } = NotificationLevel.Default;
    public MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; } = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;
    public bool MainlandChinaUrlBlockingEnabled { get; set; }
    public string ConnectionTestUrl { get; set; } = "https://www.google.com/generate_204";
    public string ConnectionTestProxyUrl1 { get; set; } = "https://www.google.com";
    public string ConnectionTestProxyUrl2 { get; set; } = "https://github.com";
    public string ConnectionTestDirectUrl { get; set; } = "https://www.baidu.com";
    public string MasterHeroStatusLayout { get; set; } = DefaultHeroLayout;
    public string MasterInfoTileLayout { get; set; } = DefaultTileLayout;
    public string MasterInfoTilePresets { get; set; } = MasterInfoTilePresetList.DefaultCanonicalText;

    public void ResetAllSettings() => CopyFrom(new PackageSettingsFake());

    public static IReadOnlyList<string> Names => [.. Properties.Select(static property => property.Name)];

    public static Type TypeOf(string name) => Properties.Single(property => property.Name == name).PropertyType;

    public Dictionary<string, string> Snapshot() =>
        Properties.ToDictionary(
            static property => property.Name,
            property => Convert.ToString(property.GetValue(this), CultureInfo.InvariantCulture) ?? string.Empty,
            StringComparer.Ordinal);

    public void CopyFrom(PackageSettingsFake other)
    {
        foreach (PropertyInfo property in Properties)
        {
            property.SetValue(this, property.GetValue(other));
        }
    }

    /// <summary>Random but valid values for every setting, driven by reflection so a new setting is covered automatically.</summary>
    public static PackageSettingsFake Random(Random random)
    {
        PackageSettingsFake settings = new();
        foreach (PropertyInfo property in Properties)
        {
            Type type = property.PropertyType;
            object? value;
            if (type == typeof(bool))
            {
                value = random.Next(2) == 0;
            }
            else if (type == typeof(int))
            {
                value = property.Name switch
                {
                    nameof(MixedPort) => random.Next(1, 65536),
                    nameof(ConnectionSamplingIntervalSeconds) => random.Next(3, 301),
                    _ => throw new NotSupportedException("Define a valid range for " + property.Name),
                };
            }
            else if (type.IsEnum)
            {
                object[] values = [.. Enum.GetValues(type).Cast<object>().Where(static value => !Equals(value, ClashSharpMode.Faulted))];
                value = values[random.Next(values.Length)];
            }
            else if (type == typeof(string))
            {
                value = property.Name is nameof(MasterHeroStatusLayout) or nameof(MasterInfoTileLayout) or nameof(MasterInfoTilePresets)
                    ? property.GetValue(settings)
                    : RandomText(random);
            }
            else
            {
                throw new NotSupportedException("Unsupported setting type " + type);
            }

            property.SetValue(settings, value);
        }

        return settings;
    }

    private static readonly string[] TextAtoms =
        ["a", "Z", "0", " ", "&", "<", ">", "\"", "'", "\t", "\n", "\r\n", "\\", "/", "香港", "😀", "עברית", "é", "%", "=", "]]>", "-->", "&amp;"];

    private static string RandomText(Random random)
    {
        StringBuilder text = new();
        int count = random.Next(0, 8);
        for (int index = 0; index < count; index++)
        {
            text.Append(TextAtoms[random.Next(TextAtoms.Length)]);
        }

        return text.ToString();
    }
}

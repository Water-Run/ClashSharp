using System.Collections.Generic;
using System.Collections.ObjectModel;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

internal sealed partial class SettingsViewModel
{
    private DataUnitStyle _dataUnitStyle = DataUnitStyle.BinaryWithDecimalLabels;
    private RateUnit _rateUnit;
    private ClockFormat _clockFormat;
    private readonly ObservableCollection<string> _dataUnitStyleOptions = [];
    private readonly ObservableCollection<string> _rateUnitOptions = [];
    private readonly ObservableCollection<string> _clockFormatOptions = [];

    public DataUnitStyle DataUnitStyle => _dataUnitStyle;
    public RateUnit RateUnit => _rateUnit;
    public ClockFormat ClockFormat => _clockFormat;
    public int DataUnitStyleIndex => (int)_dataUnitStyle;
    public int RateUnitIndex => (int)_rateUnit;
    public int ClockFormatIndex => (int)_clockFormat;
    public IReadOnlyList<string> DataUnitStyleOptions => _dataUnitStyleOptions;
    public IReadOnlyList<string> RateUnitOptions => _rateUnitOptions;
    public IReadOnlyList<string> ClockFormatOptions => _clockFormatOptions;
    public string DataUnitStyleTitleText => _getString("Settings.DataUnitStyle.Title");
    public string DataUnitStyleDescriptionText => _getString("Settings.DataUnitStyle.Description");
    public string RateUnitTitleText => _getString("Settings.RateUnit.Title");
    public string RateUnitDescriptionText => _getString("Settings.RateUnit.Description");
    public string ClockFormatTitleText => _getString("Settings.ClockFormat.Title");
    public string ClockFormatDescriptionText => _getString("Settings.ClockFormat.Description");

    private void LoadFormattingPreferences() => PublishPreferenceChanges(_settings.ReadPreferenceChanges(
        [SettingsRegistry.Keys.DataUnitStyle, SettingsRegistry.Keys.RateUnit, SettingsRegistry.Keys.ClockFormat]));

    private void RefreshFormattingOptions()
    {
        ReplaceStableOptions(_dataUnitStyleOptions, [_getString("Settings.DataUnitStyle.Si"),
            _getString("Settings.DataUnitStyle.Binary"), _getString("Settings.DataUnitStyle.Legacy")]);
        ReplaceStableOptions(_rateUnitOptions, [_getString("Settings.RateUnit.Bytes"), _getString("Settings.RateUnit.Bits")]);
        ReplaceStableOptions(_clockFormatOptions, [_getString("Settings.ClockFormat.System"),
            _getString("Settings.ClockFormat.Twelve"), _getString("Settings.ClockFormat.TwentyFour")]);
    }
}

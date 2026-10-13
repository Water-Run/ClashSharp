using System.Threading;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Service;

public sealed partial class AppSettingsService
{
    /// <summary>Gets or sets the scale and labels for displayed data quantities.</summary>
    public DataUnitStyle DataUnitStyle
    {
        get => GetEnum("DataUnitStyle", DataUnitStyle.BinaryWithDecimalLabels);
        set => WriteOrdinary(editor => editor.DataUnitStyle = value);
    }

    /// <summary>Gets or sets the numerator used to display transfer rates.</summary>
    public RateUnit RateUnit
    {
        get => GetEnum("RateUnit", RateUnit.Bytes);
        set => WriteOrdinary(editor => editor.RateUnit = value);
    }

    /// <summary>Gets or sets the display clock without changing the system locale.</summary>
    public ClockFormat ClockFormat
    {
        get => GetEnum("ClockFormat", ClockFormat.FollowSystem);
        set => WriteOrdinary(editor => editor.ClockFormat = value);
    }

    /// <summary>Reads one verified presentation snapshot, never projecting a failed desired change as applied.</summary>
    internal DisplayFormattingOptions ReadDisplayFormatting()
    {
        if (Volatile.Read(ref _authority) is not { } authority) { return new(DataUnitStyle, RateUnit, ClockFormat); }
        SettingsEnvelope envelope = CaptureReadableAuthoritySnapshot(authority).Envelope;
        return new(Read<DataUnitStyle>(SettingsRegistry.Keys.DataUnitStyle), Read<RateUnit>(SettingsRegistry.Keys.RateUnit),
            Read<ClockFormat>(SettingsRegistry.Keys.ClockFormat));

        T Read<T>(SettingKey key) where T : notnull => envelope.Applied[key].Value is { } value
            ? value.Get<T>() : SettingsRegistry.Default.Get(key.Value).DefaultValue.Get<T>();
    }
}

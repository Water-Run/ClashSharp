using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Formats display values without changing stored bytes, timestamps or process culture.</summary>
/// <remarks>Each call reads one immutable preference snapshot. Machine serialization must never use this formatter.</remarks>
public sealed class DisplayFormatter
{
    private readonly Func<DisplayFormattingOptions> _readOptions;
    private readonly Func<CultureInfo> _readCulture;

    /// <summary>Creates a formatter with optional preference and regional-format readers.</summary>
    /// <param name="readOptions">Reads applied preferences; omission uses the default Windows-style units.</param>
    /// <param name="readCulture">Reads regional formats including user overrides; omission uses current culture.</param>
    public DisplayFormatter(Func<DisplayFormattingOptions>? readOptions = null, Func<CultureInfo>? readCulture = null)
    {
        _readOptions = readOptions ?? (() => new());
        _readCulture = readCulture ?? (() => CultureInfo.CurrentCulture);
    }

    /// <summary>Freezes the preference choices for an open quantity editor while retaining its regional-format reader.</summary>
    public DisplayFormatter CaptureOptions()
    {
        DisplayFormattingOptions options = _readOptions();
        return new(() => options, _readCulture);
    }

    /// <summary>Formats a nonnegative byte quantity using the selected scale and regional number format.</summary>
    /// <param name="bytes">Raw byte count; negative observations are clamped to zero.</param>
    /// <param name="numberFormat">Standard .NET numeric display format.</param>
    public string Bytes(decimal bytes, string numberFormat = "0.##") =>
        FormatQuantity(bytes, _readOptions().DataUnitStyle, bits: false, numberFormat);

    /// <summary>Gets the selected label for a megabyte-scale quantity input.</summary>
    public string MegaUnitLabel => _readOptions().DataUnitStyle == DataUnitStyle.Binary ? "MiB" : "MB";

    /// <summary>Converts a quantity input in selected mega units to raw bytes.</summary>
    /// <param name="value">Nonnegative quantity entered by the user.</param>
    public long FromMegaUnits(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        long scale = _readOptions().DataUnitStyle == DataUnitStyle.Si ? 1000 : 1024;
        return checked(value * scale * scale);
    }

    /// <summary>Formats a raw byte-per-second rate, converting to bits when requested.</summary>
    /// <param name="bytesPerSecond">Raw byte rate.</param>
    public string Rate(long bytesPerSecond)
    {
        DisplayFormattingOptions options = _readOptions();
        bool bits = options.RateUnit == RateUnit.Bits;
        decimal quantity = Math.Max(0m, bytesPerSecond) * (bits ? 8 : 1);
        return FormatQuantity(quantity, options.DataUnitStyle, bits, "0.##") + "/s";
    }

    /// <summary>Formats a local display timestamp with the selected clock and regional date pattern.</summary>
    /// <param name="timestamp">Canonical timestamp to display in the machine's local time zone.</param>
    /// <param name="includeSeconds">Whether to retain seconds.</param>
    public string Timestamp(DateTimeOffset timestamp, bool includeSeconds = true) =>
        timestamp.ToLocalTime().ToString(includeSeconds ? "G" : "g", ClockCulture());

    /// <summary>Formats an optional timestamp while preserving an absent observation.</summary>
    /// <param name="timestamp">Optional canonical timestamp.</param>
    /// <param name="includeSeconds">Whether to retain seconds.</param>
    public string? Timestamp(DateTimeOffset? timestamp, bool includeSeconds = true) =>
        timestamp.HasValue ? Timestamp(timestamp.Value, includeSeconds) : null;

    /// <summary>Formats a wall-clock condition, retaining subsecond precision when present.</summary>
    /// <param name="time">Local wall-clock time.</param>
    public string Time(TimeOnly time)
    {
        CultureInfo culture = ClockCulture();
        string pattern = culture.DateTimeFormat.LongTimePattern;
        if (time.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            pattern = WithFractionalSeconds(pattern);
        }
        return time.ToString(pattern, culture);
    }

    // Custom time patterns contain quoted literals, escaped characters and percent-prefixed
    // single tokens. Edit format tokens only; literal words such as 'class' are user content.
    private static string WithFractionalSeconds(string pattern)
    {
        int secondsEnd = -1;
        int minutesEnd = -1;
        int hoursEnd = -1;
        int designatorStart = -1;
        int fractionStart = -1;
        int fractionLength = 0;
        char fractionKind = 'F';
        char quote = '\0';
        for (int index = 0; index < pattern.Length; index++)
        {
            char token = pattern[index];
            if (token == '\\') { index++; continue; }
            if (quote != '\0')
            {
                if (token == quote) { quote = '\0'; }
                continue;
            }
            if (token is '\'' or '"') { quote = token; continue; }
            int start = index;
            if (token == '%' && index + 1 < pattern.Length) { token = pattern[++index]; }
            else { while (index + 1 < pattern.Length && pattern[index + 1] == token) { index++; } }
            int end = index + 1;
            switch (token)
            {
                case 's' when secondsEnd < 0: secondsEnd = end; break;
                case 'm': minutesEnd = end; break;
                case 'h' or 'H': hoursEnd = end; break;
                case 't': designatorStart = start; break;
                case 'f' or 'F' when fractionStart < 0:
                    fractionStart = start;
                    fractionLength = end - start;
                    fractionKind = token;
                    break;
            }
        }

        if (fractionStart >= 0)
        {
            return pattern[..fractionStart] + new string(fractionKind, 7) + pattern[(fractionStart + fractionLength)..];
        }
        if (secondsEnd >= 0) { return pattern.Insert(secondsEnd, ".FFFFFFF"); }

        // A system override can hide seconds. A fractional wall-clock condition must still
        // disclose its precision; retain the regional prefix/suffix and insert clock fields.
        int lastClockEnd = Math.Max(minutesEnd, hoursEnd);
        if (lastClockEnd < 0) { return "HH:mm:ss.FFFFFFF"; }
        string seconds = minutesEnd >= 0 ? ":ss.FFFFFFF" : ":mm:ss.FFFFFFF";
        return designatorStart >= lastClockEnd
            ? pattern[..designatorStart].TrimEnd() + seconds + " " + pattern[designatorStart..]
            : pattern + seconds;
    }

    private string FormatQuantity(decimal quantity, DataUnitStyle style, bool bits, string numberFormat)
    {
        string[] units = bits
            ? style == DataUnitStyle.Binary ? ["b", "Kib", "Mib", "Gib", "Tib", "Pib", "Eib"] : ["b", "kb", "Mb", "Gb", "Tb", "Pb", "Eb"]
            : style == DataUnitStyle.Binary ? ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"] : ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        int scale = style == DataUnitStyle.Si ? 1000 : 1024;
        decimal value = Math.Max(0m, quantity);
        int unit = 0;
        while (value >= scale && unit < units.Length - 1)
        {
            value /= scale;
            unit++;
        }
        return value.ToString(numberFormat, _readCulture()) + " " + units[unit];
    }

    private CultureInfo ClockCulture()
    {
        CultureInfo regional = _readCulture();
        ClockFormat clock = _readOptions().ClockFormat;
        if (clock == ClockFormat.FollowSystem) { return regional; }
        CultureInfo custom = (CultureInfo)regional.Clone();
        custom.DateTimeFormat.ShortTimePattern = clock == ClockFormat.TwelveHour ? "h:mm tt" : "HH:mm";
        custom.DateTimeFormat.LongTimePattern = clock == ClockFormat.TwelveHour ? "h:mm:ss tt" : "HH:mm:ss";
        return custom;
    }
}

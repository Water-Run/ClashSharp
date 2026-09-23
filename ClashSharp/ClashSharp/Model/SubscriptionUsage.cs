using System;
using System.Globalization;

namespace ClashSharp.Model;

/// <summary>Optional provider-reported subscription counters; missing values remain unknown.</summary>
/// <param name="UploadBytes">Non-negative uploaded bytes, or null when unavailable.</param>
/// <param name="DownloadBytes">Non-negative downloaded bytes, or null when unavailable.</param>
/// <param name="TotalBytes">Non-negative quota bytes, or null when unavailable.</param>
/// <param name="ExpireUnixSeconds">Unix expiry time; zero means no expiry and null means unavailable.</param>
public sealed record SubscriptionUsage(long? UploadBytes, long? DownloadBytes, long? TotalBytes, long? ExpireUnixSeconds)
{
    /// <summary>Parses bounded Subscription-Userinfo metadata without trusting malformed counters.</summary>
    internal static SubscriptionUsage? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 8192) { return null; }
        long? upload = null;
        long? download = null;
        long? total = null;
        long? expire = null;
        foreach (string field in header.Split(';', StringSplitOptions.TrimEntries))
        {
            int separator = field.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0) { continue; }
            string name = field[..separator].Trim();
            long? value = ParseCounter(field[(separator + 1)..]);
            if (name.Equals("upload", StringComparison.OrdinalIgnoreCase)) { upload = value; }
            else if (name.Equals("download", StringComparison.OrdinalIgnoreCase)) { download = value; }
            else if (name.Equals("total", StringComparison.OrdinalIgnoreCase)) { total = value; }
            else if (name.Equals("expire", StringComparison.OrdinalIgnoreCase))
            {
                expire = value <= 253402300799 ? value : null;
            }
        }

        return upload is null && download is null && total is null && expire is null
            ? null : new SubscriptionUsage(upload, download, total, expire);
    }

    private static long? ParseCounter(string text)
    {
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            && value >= 0 && value <= long.MaxValue
            ? (long)decimal.Truncate(value) : null;
    }
}

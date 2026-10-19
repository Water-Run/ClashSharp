using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using ClashSharp.Strings;

namespace ClashSharp.Service;

/// <summary>Stores language-independent subscription status identities and recognizes earlier localized catalog values.</summary>
internal static class SubscriptionStatusText
{
    private const string CheckSucceededFormat = "ProfileCatalog.Subscription.CheckSucceeded.Format";
    private const string CheckSucceededPrefix = CheckSucceededFormat + "|";
    private static readonly string[] Keys =
    [
        "ProfileCatalog.Status.Added", ProfileStatusText.Canceled,
        "ProfileCatalog.Subscription.CheckFailed", "ProfileCatalog.Subscription.Downloading",
        "ProfileCatalog.Subscription.Updated", "ProfileCatalog.Subscription.UpdateFailed",
    ];
    private static readonly FrozenDictionary<string, string> KnownStatuses = BuildKnownStatuses();
    private static readonly (string Prefix, string Suffix)[] LegacyCheckFormats = BuildCheckFormats();

    /// <summary>Creates one stable successful-check status without storing localized text.</summary>
    public static string CheckSucceeded(int statusCode) =>
        CheckSucceededPrefix + statusCode.ToString(CultureInfo.InvariantCulture);

    /// <summary>Recognizes only authored status text; unknown diagnostics remain unchanged.</summary>
    public static string Normalize(string? status)
    {
        if (status is null) { return string.Empty; }
        if (KnownStatuses.TryGetValue(status, out string? key)) { return key; }
        if (TryReadCode(status, CheckSucceededPrefix, string.Empty, out int canonical)) { return CheckSucceeded(canonical); }
        foreach ((string prefix, string suffix) in LegacyCheckFormats)
        {
            if (TryReadCode(status, prefix, suffix, out int code)) { return CheckSucceeded(code); }
        }
        return status;
    }

    /// <summary>Projects the current language without changing persisted provider metadata or catalog bytes.</summary>
    public static string Localize(string? status, Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        string normalized = Normalize(status);
        if (KnownStatuses.ContainsKey(normalized)) { return getString(normalized); }
        return TryReadCode(normalized, CheckSucceededPrefix, string.Empty, out int code)
            ? string.Format(CultureInfo.CurrentCulture, getString(CheckSucceededFormat), code)
            : normalized;
    }

    private static bool TryReadCode(string text, string prefix, string suffix, out int code)
    {
        code = 0;
        int length = text.Length - prefix.Length - suffix.Length;
        return length == 3 && text.StartsWith(prefix, StringComparison.Ordinal) && text.EndsWith(suffix, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(prefix.Length, length), NumberStyles.None, CultureInfo.InvariantCulture, out code)
            && code is >= 100 and <= 999;
    }

    private static FrozenDictionary<string, string> BuildKnownStatuses()
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string key in Keys)
        {
            values[key] = key;
            foreach (IReadOnlyDictionary<string, string> catalog in LocalizationResources.Translations.Values) { values[catalog[key]] = key; }
        }
        return values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static (string Prefix, string Suffix)[] BuildCheckFormats()
    {
        List<(string, string)> formats = [];
        foreach (IReadOnlyDictionary<string, string> catalog in LocalizationResources.Translations.Values)
        {
            string template = catalog[CheckSucceededFormat];
            int index = template.IndexOf("{0}", StringComparison.Ordinal);
            if (index >= 0) { formats.Add((template[..index], template[(index + 3)..])); }
        }
        return [.. formats];
    }
}

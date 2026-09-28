using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using ClashSharp.Strings;

namespace ClashSharp.Service;

/// <summary>Stores stable profile status keys and recognizes text saved by earlier catalogs.</summary>
internal static class ProfileStatusText
{
    public const string Available = "ProfileCatalog.Status.Available";
    public const string Canceled = "ProfileCatalog.Status.Canceled";
    public const string Validated = "ProfileCatalog.Profile.ValidationSucceeded";
    public const string Invalid = "ProfileCatalog.Profile.ValidationFailed";

    private static readonly FrozenDictionary<string, string> KnownStatuses = BuildKnownStatuses();

    public static string Normalize(string? status) =>
        status is not null && KnownStatuses.TryGetValue(status, out string? key) ? key : status ?? string.Empty;

    public static string Localize(string? status, Func<string, string> getString) =>
        status is not null && KnownStatuses.TryGetValue(status, out string? key) ? getString(key) : status ?? string.Empty;

    private static FrozenDictionary<string, string> BuildKnownStatuses()
    {
        Dictionary<string, string> statuses = new(StringComparer.Ordinal);
        foreach (string key in new[] { Available, Canceled, Validated, Invalid })
        {
            statuses.Add(key, key);
            foreach (IReadOnlyDictionary<string, string> catalog in LocalizationResources.Translations.Values)
            {
                statuses[catalog[key]] = key;
            }
        }

        return statuses.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

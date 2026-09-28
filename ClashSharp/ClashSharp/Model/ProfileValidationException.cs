using System;
using System.Collections.Generic;

namespace ClashSharp.Model;

/// <summary>Required configuration sections that are absent from an imported profile.</summary>
[Flags]
internal enum MissingProfileSections
{
    None = 0,
    ProxySource = 1,
    ProxyGroups = 2,
    Rules = 4,
}

/// <summary>Reports missing profile sections using fixed identifiers rather than file content.</summary>
internal sealed class ProfileValidationException : ArgumentException
{
    public ProfileValidationException(MissingProfileSections missingSections)
        : base("Configuration is missing required top-level sections.", "configurationText")
    {
        MissingSections = missingSections;
    }

    public MissingProfileSections MissingSections { get; }

    /// <summary>Returns configuration keys safe to include in localized user feedback.</summary>
    public string GetMissingSectionNames()
    {
        List<string> names = [];
        if (MissingSections.HasFlag(MissingProfileSections.ProxySource)) { names.Add("proxies / proxy-providers"); }
        if (MissingSections.HasFlag(MissingProfileSections.ProxyGroups)) { names.Add("proxy-groups"); }
        if (MissingSections.HasFlag(MissingProfileSections.Rules)) { names.Add("rules"); }
        return string.Join(", ", names);
    }
}

/*
 * Mihomo Profile Preview Parser
 * Provides pure conservative preview parsing for mihomo proxy nodes, providers, and rules
 *
 * @author: WaterRun
 * @file: Service/MihomoProfilePreviewParser.cs
 * @date: 2026-06-17
 */

using System;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides pure conservative preview parsing for mihomo profile text.</summary>
/// <remarks>
/// Invariants: Parsing is best-effort and returns partial preview rows instead of validating full YAML correctness.
/// Thread safety: Stateless methods are safe for concurrent calls.
/// Side effects: None.
/// </remarks>
internal static class MihomoProfilePreviewParser
{
    /// <summary>Parses proxy node and provider preview rows from profile text.</summary>
    /// <param name="configurationText">mihomo configuration text. Must not be null.</param>
    /// <param name="resolveRegion">Region resolver used to convert inferred region codes into display metadata. Must not be null.</param>
    /// <returns>Parsed proxy node preview rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> or <paramref name="resolveRegion"/> is null.</exception>
    public static IReadOnlyList<ProxyNode> ParseNodes(string configurationText, Func<string, RegionMetadata> resolveRegion)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        ArgumentNullException.ThrowIfNull(resolveRegion);

        List<ProxyNode> nodes = [];
        foreach (YamlListItem item in EnumerateTopLevelListItems(configurationText, "proxies"))
        {
            string name = item.GetScalar("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string protocol = item.GetScalar("type");
            if (string.IsNullOrWhiteSpace(protocol))
            {
                protocol = "UNKNOWN";
            }

            string serverHost = item.GetScalar("server");
            int? serverPort = TryParsePort(item.GetScalar("port"));
            string regionCode = InferRegionCode(name + " " + serverHost);
            nodes.Add(new ProxyNode(
                name,
                protocol.ToUpperInvariant(),
                resolveRegion(regionCode),
                null,
                serverHost,
                serverPort));
        }

        foreach (YamlMapItem item in EnumerateTopLevelMapItems(configurationText, "proxy-providers"))
        {
            string providerName = item.Name;
            if (string.IsNullOrWhiteSpace(providerName))
            {
                continue;
            }

            string providerType = item.GetScalar("type");
            if (string.IsNullOrWhiteSpace(providerType))
            {
                providerType = "provider";
            }

            string url = item.GetScalar("url");
            string regionCode = InferRegionCode(providerName + " " + url);
            nodes.Add(new ProxyNode(
                providerName,
                $"PROVIDER/{providerType.ToUpperInvariant()}",
                resolveRegion(regionCode),
                null,
                TryGetHostFromUri(url),
                null));
        }

        return nodes;
    }

    /// <summary>Parses routing rule preview rows from profile text.</summary>
    /// <param name="configurationText">mihomo configuration text. Must not be null.</param>
    /// <returns>Parsed routing rule preview rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> is null.</exception>
    public static IReadOnlyList<RulePreview> ParseRules(string configurationText)
    {
        ArgumentNullException.ThrowIfNull(configurationText);

        List<RulePreview> rules = [];
        foreach (YamlListItem item in EnumerateTopLevelListItems(configurationText, "rules"))
        {
            string rawRule = item.RawValue.Trim();
            if (string.IsNullOrWhiteSpace(rawRule))
            {
                continue;
            }

            string[] parts = rawRule.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            string ruleType = parts[0];
            string payload = parts.Length > 2 ? parts[1] : "*";
            string action = parts.Length > 2 ? parts[2] : parts.Length > 1 ? parts[1] : "DIRECT";
            rules.Add(new RulePreview("当前配置", ruleType, payload, action, 0));
        }

        return rules;
    }

    /// <summary>Enumerates top-level YAML list items from a named section using a conservative scanner.</summary>
    /// <param name="configurationText">Configuration text. Must not be null.</param>
    /// <param name="sectionName">Top-level section name. Must not be null.</param>
    /// <returns>Parsed list item rows from the requested section.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> or <paramref name="sectionName"/> is null.</exception>
    private static IEnumerable<YamlListItem> EnumerateTopLevelListItems(string configurationText, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        ArgumentNullException.ThrowIfNull(sectionName);

        string[] lines = NormalizeLineEndings(configurationText).Split('\n');
        bool inSection = false;
        YamlListItem? currentItem = null;
        int sectionIndent = 0;

        foreach (string line in lines)
        {
            if (!inSection)
            {
                if (IsTopLevelSection(line, sectionName))
                {
                    inSection = true;
                    sectionIndent = CountLeadingSpaces(line);
                }

                continue;
            }

            if (IsTopLevelBoundary(line, sectionIndent))
            {
                if (currentItem is not null)
                {
                    yield return currentItem;
                }

                yield break;
            }

            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (currentItem is not null)
                {
                    TryAddKeyValue(currentItem, trimmed);
                }

                continue;
            }

            if (currentItem is not null)
            {
                yield return currentItem;
            }

            currentItem = new YamlListItem(trimmed[2..].Trim());
        }

        if (currentItem is not null)
        {
            yield return currentItem;
        }
    }

    /// <summary>Enumerates top-level YAML mapping items from a named section using a conservative scanner.</summary>
    /// <param name="configurationText">Configuration text. Must not be null.</param>
    /// <param name="sectionName">Top-level section name. Must not be null.</param>
    /// <returns>Parsed mapping item rows from the requested section.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> or <paramref name="sectionName"/> is null.</exception>
    private static IEnumerable<YamlMapItem> EnumerateTopLevelMapItems(string configurationText, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        ArgumentNullException.ThrowIfNull(sectionName);

        string[] lines = NormalizeLineEndings(configurationText).Split('\n');
        bool inSection = false;
        YamlMapItem? currentItem = null;
        int sectionIndent = 0;
        int itemIndent = -1;

        foreach (string line in lines)
        {
            if (!inSection)
            {
                if (IsTopLevelSection(line, sectionName))
                {
                    inSection = true;
                    sectionIndent = CountLeadingSpaces(line);
                }

                continue;
            }

            if (IsTopLevelBoundary(line, sectionIndent))
            {
                if (currentItem is not null)
                {
                    yield return currentItem;
                }

                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int indent = CountLeadingSpaces(line);
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            int separatorIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            bool startsNewItem = indent > sectionIndent && (itemIndent < 0 || indent <= itemIndent);
            if (startsNewItem)
            {
                if (currentItem is not null)
                {
                    yield return currentItem;
                }

                itemIndent = indent;
                string name = TrimYamlScalar(trimmed[..separatorIndex]);
                currentItem = new YamlMapItem(name);
                string inlineValue = trimmed[(separatorIndex + 1)..].Trim();
                currentItem.ParseInlineObject(inlineValue);
                continue;
            }

            if (currentItem is not null)
            {
                TryAddKeyValue(currentItem, trimmed);
            }
        }

        if (currentItem is not null)
        {
            yield return currentItem;
        }
    }

    /// <summary>Returns whether a line starts the requested top-level YAML section.</summary>
    /// <param name="line">Input line. Must not be null.</param>
    /// <param name="sectionName">Section name. Must not be null.</param>
    /// <returns>True when the line starts the section.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> or <paramref name="sectionName"/> is null.</exception>
    private static bool IsTopLevelSection(string line, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(sectionName);

        return line.StartsWith(sectionName + ":", StringComparison.Ordinal);
    }

    /// <summary>Returns whether a line exits the current top-level YAML section.</summary>
    /// <param name="line">Input line. Must not be null.</param>
    /// <param name="sectionIndent">Current section indentation.</param>
    /// <returns>True when the line is a non-empty top-level boundary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    private static bool IsTopLevelBoundary(string line, int sectionIndent)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int indent = CountLeadingSpaces(line);
        return indent <= sectionIndent && !line.TrimStart().StartsWith("- ", StringComparison.Ordinal);
    }

    /// <summary>Attempts to add a key-value line to a parsed YAML list item.</summary>
    /// <param name="item">Current YAML list item. Must not be null.</param>
    /// <param name="line">Trimmed YAML line. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> or <paramref name="line"/> is null.</exception>
    private static void TryAddKeyValue(YamlListItem item, string line)
    {
        ArgumentNullException.ThrowIfNull(item);
        TryAddKeyValue(item.Values, line);
    }

    /// <summary>Attempts to add a key-value line to a parsed YAML mapping item.</summary>
    /// <param name="item">Current YAML mapping item. Must not be null.</param>
    /// <param name="line">Trimmed YAML line. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> or <paramref name="line"/> is null.</exception>
    private static void TryAddKeyValue(YamlMapItem item, string line)
    {
        ArgumentNullException.ThrowIfNull(item);
        TryAddKeyValue(item.Values, line);
    }

    /// <summary>Attempts to add a key-value line to a scalar dictionary.</summary>
    /// <param name="values">Target scalar dictionary. Must not be null.</param>
    /// <param name="line">Trimmed YAML line. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or <paramref name="line"/> is null.</exception>
    private static void TryAddKeyValue(Dictionary<string, string> values, string line)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(line);

        int separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return;
        }

        string key = line[..separatorIndex].Trim();
        string value = line[(separatorIndex + 1)..].Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            values[key] = TrimYamlScalar(value);
        }
    }

    /// <summary>Infers a node region from common node names and server hints.</summary>
    /// <param name="text">Node name and server hint text. Must not be null.</param>
    /// <returns>Best-effort region code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    private static string InferRegionCode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string lower = text.ToLowerInvariant();
        return lower switch
        {
            _ when ContainsAny(lower, "hk", "hong kong", "hongkong") => "HK",
            _ when ContainsAny(lower, "mo", "macao", "macau") => "MO",
            _ when ContainsAny(lower, "tw", "taiwan", "taipei") => "TW",
            _ when ContainsAny(lower, "jp", "japan", "tokyo", "osaka") => "JP",
            _ when ContainsAny(lower, "kr", "korea", "seoul") => "KR",
            _ when ContainsAny(lower, "sg", "singapore") => "SG",
            _ when ContainsAny(lower, "us", "usa", "united states", "america", "los angeles", "new york") => "US",
            _ when ContainsAny(lower, "gb", "uk", "united kingdom", "london") => "GB",
            _ when ContainsAny(lower, "de", "germany", "frankfurt") => "DE",
            _ when ContainsAny(lower, "fr", "france", "paris") => "FR",
            _ when ContainsAny(lower, "cn", "china", "mainland") => "CN",
            _ => "UN",
        };
    }

    /// <summary>Parses a TCP port value from YAML scalar text.</summary>
    /// <param name="value">Port scalar text. Must not be null.</param>
    /// <returns>Parsed port in range [1, 65535], otherwise null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    private static int? TryParsePort(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return int.TryParse(value, out int port) && port is >= 1 and <= 65535 ? port : null;
    }

    /// <summary>Returns whether <paramref name="text"/> contains any candidate string.</summary>
    /// <param name="text">Search text. Must not be null.</param>
    /// <param name="candidates">Candidate substrings. Must not be null.</param>
    /// <returns>True when a candidate is present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="candidates"/> is null.</exception>
    private static bool ContainsAny(string text, params string[] candidates)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(candidates);

        foreach (string candidate in candidates)
        {
            if (text.Contains(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Extracts a host from a URI string when possible.</summary>
    /// <param name="value">Potential absolute URI. Must not be null.</param>
    /// <returns>URI host when available; otherwise an empty string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    private static string TryGetHostFromUri(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri.Host : string.Empty;
    }

    /// <summary>Splits inline YAML object pairs while respecting simple quoted text.</summary>
    /// <param name="body">Inline object body. Must not be null.</param>
    /// <returns>Pair text segments.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    private static IEnumerable<string> SplitInlinePairs(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        int start = 0;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        for (int index = 0; index < body.Length; index++)
        {
            char character = body[index];
            if (character == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == ',' && !inSingleQuote && !inDoubleQuote)
            {
                yield return body[start..index];
                start = index + 1;
            }
        }

        yield return body[start..];
    }

    /// <summary>Normalizes line endings to LF.</summary>
    /// <param name="text">Input text. Must not be null.</param>
    /// <returns>Normalized text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    private static string NormalizeLineEndings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>Counts leading spaces in a YAML line.</summary>
    /// <param name="line">Input line. Must not be null.</param>
    /// <returns>Leading space count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    private static int CountLeadingSpaces(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        int count = 0;
        foreach (char character in line)
        {
            if (character != ' ')
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>Trims quotes and inline comments from a YAML scalar value.</summary>
    /// <param name="value">Scalar value. Must not be null.</param>
    /// <returns>Trimmed scalar value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    private static string TrimYamlScalar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string trimmed = value.Trim();
        int commentIndex = trimmed.IndexOf(" #", StringComparison.Ordinal);
        if (commentIndex >= 0)
        {
            trimmed = trimmed[..commentIndex].Trim();
        }

        return trimmed.Trim('"', '\'');
    }

    /// <summary>Parsed YAML list item with simple scalar access.</summary>
    /// <remarks>
    /// Invariants: Raw value and scalar dictionary are never null.
    /// Thread safety: Not thread-safe; each item is confined to one parser invocation.
    /// Side effects: None.
    /// </remarks>
    private sealed class YamlListItem
    {
        /// <summary>Initializes a parsed YAML list item.</summary>
        /// <param name="rawValue">Raw list item value. Must not be null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rawValue"/> is null.</exception>
        public YamlListItem(string rawValue)
        {
            ArgumentNullException.ThrowIfNull(rawValue);
            RawValue = TrimYamlScalar(rawValue);
            ParseInlineObject(RawValue);
            ParseScalarKeyValue(RawValue);
        }

        /// <summary>Gets the raw scalar value for the list item.</summary>
        /// <value>Raw list item text; never null.</value>
        public string RawValue { get; }

        /// <summary>Gets parsed key-value scalars for object-shaped list items.</summary>
        /// <value>Mutable key-value map for the parser lifetime; never null.</value>
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets a scalar value by key.</summary>
        /// <param name="key">Scalar key. Must not be null.</param>
        /// <returns>Scalar value when present; otherwise an empty string.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
        public string GetScalar(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return Values.TryGetValue(key, out string? value) ? value : string.Empty;
        }

        /// <summary>Parses one-line YAML object syntax such as {name: node, type: ss}.</summary>
        /// <param name="rawValue">Raw value to parse. Must not be null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rawValue"/> is null.</exception>
        private void ParseInlineObject(string rawValue)
        {
            ArgumentNullException.ThrowIfNull(rawValue);

            string trimmed = rawValue.Trim();
            if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
            {
                return;
            }

            string body = trimmed[1..^1];
            foreach (string pair in SplitInlinePairs(body))
            {
                int separatorIndex = pair.IndexOf(':', StringComparison.Ordinal);
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = pair[..separatorIndex].Trim();
                string value = pair[(separatorIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    Values[key] = TrimYamlScalar(value);
                }
            }
        }

        /// <summary>Parses block-list first-line syntax such as name: node.</summary>
        /// <param name="rawValue">Raw value to parse. Must not be null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rawValue"/> is null.</exception>
        private void ParseScalarKeyValue(string rawValue)
        {
            ArgumentNullException.ThrowIfNull(rawValue);

            if (rawValue.StartsWith('{') && rawValue.EndsWith('}'))
            {
                return;
            }

            int separatorIndex = rawValue.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return;
            }

            string key = rawValue[..separatorIndex].Trim();
            string value = rawValue[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                Values[key] = TrimYamlScalar(value);
            }
        }
    }

    /// <summary>Parsed YAML mapping item with simple scalar access.</summary>
    /// <remarks>
    /// Invariants: Name and scalar dictionary are never null.
    /// Thread safety: Not thread-safe; each item is confined to one parser invocation.
    /// Side effects: None.
    /// </remarks>
    private sealed class YamlMapItem
    {
        /// <summary>Initializes a parsed YAML mapping item.</summary>
        /// <param name="name">Mapping item name. Must not be null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        public YamlMapItem(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            Name = name;
        }

        /// <summary>Gets the mapping item name.</summary>
        /// <value>Mapping key text; never null.</value>
        public string Name { get; }

        /// <summary>Gets parsed key-value scalars for the mapping item.</summary>
        /// <value>Mutable key-value map for the parser lifetime; never null.</value>
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets a scalar value by key.</summary>
        /// <param name="key">Scalar key. Must not be null.</param>
        /// <returns>Scalar value when present; otherwise an empty string.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
        public string GetScalar(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return Values.TryGetValue(key, out string? value) ? value : string.Empty;
        }

        /// <summary>Parses one-line YAML object syntax such as {type: http, url: https://example.invalid}.</summary>
        /// <param name="rawValue">Raw value to parse. Must not be null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rawValue"/> is null.</exception>
        public void ParseInlineObject(string rawValue)
        {
            ArgumentNullException.ThrowIfNull(rawValue);

            string trimmed = rawValue.Trim();
            if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
            {
                return;
            }

            string body = trimmed[1..^1];
            foreach (string pair in SplitInlinePairs(body))
            {
                TryAddKeyValue(this, pair.Trim());
            }
        }
    }
}

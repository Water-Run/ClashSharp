using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Offline region names and bounded node-name hints for all packaged country flags.</summary>
internal static partial class RegionCatalog
{
    private static readonly FrozenDictionary<string, string[]> Countries = CreateCountryData()
        .ToFrozenDictionary(static country => country.Code, static country => country.Names, StringComparer.OrdinalIgnoreCase);

    private static readonly (string Hint, string Code)[] Aliases = BuildAliases();

    [GeneratedRegex(@"(?<![A-Za-z])[A-Za-z]{2}(?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex CountryTokens();

    internal static IReadOnlyCollection<string> Codes => Countries.Keys;
    internal static bool Contains(string code) => Countries.ContainsKey(code);

    internal static string GetDisplayName(string code, AppLanguage language) => Countries.TryGetValue(code, out string[]? names)
        ? names[(int)language is >= 0 and <= 7 ? (int)language : 2] : code;

    internal static string Infer(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? mainlandHint = null;
        foreach ((string hint, string code) in Aliases)
        {
            int offset = 0;
            while ((offset = text.IndexOf(hint, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = offset + hint.Length;
                bool cjk = hint.Any(static character => character is >= '\u3400' and <= '\u9fff');
                if (cjk || (offset == 0 || !char.IsLetter(text[offset - 1])) && (end == text.Length || !char.IsLetter(text[end])))
                {
                    // Names such as 中国香港 contain a mainland hint too; the more
                    // specific named location must win over that general qualifier.
                    if (code != "CN") { return code; }
                    mainlandHint = code;
                    break;
                }
                offset = end;
            }
        }
        if (mainlandHint is not null) { return mainlandHint; }
        foreach (Match token in CountryTokens().Matches(text))
        {
            string code = token.Value.ToUpperInvariant();
            if (Countries.ContainsKey(code) && (token.Value == code || token.Index == 0
                || code is "HK" or "MO" or "TW" or "JP" or "KR" or "SG" or "US" or "GB" or "DE" or "FR" or "CN"
                || token.Index > 0 && text[token.Index - 1] == '.')) { return code; }
        }
        int? first = null;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                if (first is int previous)
                {
                    string code = string.Concat((char)('A' + previous - 0x1F1E6), (char)('A' + rune.Value - 0x1F1E6));
                    if (Countries.ContainsKey(code)) { return code; }
                }
                first = rune.Value;
            }
            else { first = null; }
        }
        return "UN";
    }

    private static (string Hint, string Code)[] BuildAliases()
    {
        List<(string Hint, string Code)> aliases = [];
        foreach ((string code, string[] names) in Countries)
        {
            aliases.AddRange(names.Where(static name => name.Length >= 3 || name.Any(static character => character > 127))
                .Select(name => (name, code)));
        }
        (string Code, string[] Hints)[] common =
        [
            ("HK", ["hong kong", "hongkong", "香港"]), ("MO", ["macao", "macau", "澳门", "澳門"]),
            ("TW", ["taipei", "台湾", "台灣", "臺灣", "台北", "臺北"]),
            ("JP", ["tokyo", "osaka", "东京", "東京", "大阪"]), ("KR", ["korea", "seoul", "韩国", "韓國", "首尔", "首爾"]),
            ("SG", ["singapore", "新加坡"]), ("US", ["usa", "america", "los angeles", "new york", "美国", "美國", "洛杉矶", "洛杉磯", "纽约", "紐約"]),
            ("GB", ["uk", "london", "英国", "英國", "伦敦", "倫敦"]), ("DE", ["frankfurt", "德国", "德國", "法兰克福", "法蘭克福"]),
            ("FR", ["paris", "法国", "法國", "巴黎"]), ("CN", ["china", "mainland", "中国", "中國", "内地", "內地", "大陆", "大陸"]),
            ("FI", ["helsinki", "芬兰", "芬蘭", "赫尔辛基", "赫爾辛基"]), ("NL", ["amsterdam", "荷兰", "荷蘭", "阿姆斯特丹"]),
            ("CA", ["toronto", "vancouver", "多伦多", "多倫多", "温哥华", "溫哥華"]), ("AU", ["sydney", "melbourne", "悉尼", "墨尔本", "墨爾本"]),
            ("RU", ["moscow", "莫斯科"]), ("TR", ["istanbul", "土耳其", "伊斯坦布尔"]), ("CH", ["zurich", "苏黎世", "蘇黎世"]),
        ];
        foreach ((string code, string[] hints) in common) { aliases.AddRange(hints.Select(hint => (hint, code))); }
        return aliases.Distinct().OrderByDescending(static alias => alias.Hint.Length).ToArray();
    }
}

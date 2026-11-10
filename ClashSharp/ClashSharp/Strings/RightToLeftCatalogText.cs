using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace ClashSharp.Strings;

/// <summary>Keeps left-to-right fragments in reading order inside right-to-left catalog text.</summary>
/// <remarks>
/// The Unicode bidirectional algorithm resolves a weak or neutral character at the edge of a left-to-right run
/// from the surrounding right-to-left text, so <c>Clash#</c> inside a Persian sentence renders as <c>#Clash</c>
/// and a node name such as <c>Hong Kong (x2)</c> loses its closing parenthesis to the sentence. The product name
/// is therefore followed by a left-to-right mark, which binds the trailing <c>#</c> to the name, and every
/// composite-format placeholder is wrapped in a first-strong isolate, so a formatted argument keeps its own
/// direction whichever script it is written in and never reorders the sentence around it.
/// Authored catalogs stay free of invisible characters; the marks are added once, when the runtime table is built.
/// </remarks>
internal static partial class RightToLeftCatalogText
{
    /// <summary>Left-to-right mark.</summary>
    internal const char LeftToRightMark = '\u200E';

    /// <summary>First-strong isolate.</summary>
    internal const char FirstStrongIsolate = '\u2068';

    /// <summary>Pop directional isolate.</summary>
    internal const char PopDirectionalIsolate = '\u2069';

    /// <summary>The product name as catalogs write it.</summary>
    internal const string ProductName = "Clash#";

    /// <summary>Prepares every value of one right-to-left catalog.</summary>
    /// <param name="catalog">Catalog to prepare.</param>
    /// <returns>A new catalog with the same keys and prepared values.</returns>
    internal static ReadOnlyDictionary<string, string> Prepare(IReadOnlyDictionary<string, string> catalog)
    {
        Dictionary<string, string> prepared = new(catalog.Count);
        foreach ((string key, string value) in catalog)
        {
            prepared.Add(key, Prepare(value));
        }

        return new ReadOnlyDictionary<string, string>(prepared);
    }

    /// <summary>Prepares one right-to-left catalog value; preparing a prepared value changes nothing.</summary>
    /// <param name="text">Authored catalog text, possibly a composite format.</param>
    /// <returns>The text with the product name bound and placeholders isolated.</returns>
    internal static string Prepare(string text)
    {
        StringBuilder builder = new(text.Length + 8);
        int index = 0;
        while (index < text.Length)
        {
            if (string.CompareOrdinal(text, index, ProductName, 0, ProductName.Length) == 0)
            {
                builder.Append(ProductName);
                index += ProductName.Length;
                if (index >= text.Length || text[index] != LeftToRightMark) { builder.Append(LeftToRightMark); }
                continue;
            }

            // Doubled braces are literal text, never the start or end of a placeholder.
            if (index + 1 < text.Length && text[index] is '{' or '}' && text[index + 1] == text[index])
            {
                builder.Append(text, index, 2);
                index += 2;
                continue;
            }

            Match placeholder = text[index] == '{' ? Placeholder().Match(text, index) : Match.Empty;
            if (placeholder.Success)
            {
                bool isolated = index > 0 && text[index - 1] == FirstStrongIsolate
                    && index + placeholder.Length < text.Length && text[index + placeholder.Length] == PopDirectionalIsolate;
                if (!isolated) { builder.Append(FirstStrongIsolate); }
                builder.Append(placeholder.Value);
                if (!isolated) { builder.Append(PopDirectionalIsolate); }
                index += placeholder.Length;
                continue;
            }

            builder.Append(text[index]);
            index++;
        }

        return builder.ToString();
    }

    // A composite-format item: index, optional alignment and optional format string.
    [GeneratedRegex(@"\G\{\d+(?:,-?\d+)?(?::[^{}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

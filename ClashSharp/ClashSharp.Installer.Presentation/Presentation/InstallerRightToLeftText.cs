using System.Text;
using System.Text.RegularExpressions;

namespace ClashSharp.Installer.Presentation;

/// <summary>Keeps left-to-right fragments in reading order inside right-to-left installer text.</summary>
/// <remarks>
/// The Unicode bidirectional algorithm resolves a weak or neutral character at the edge of a left-to-right run
/// from the surrounding right-to-left text, so <c>Clash#</c> inside a Persian sentence renders as <c>#Clash</c>
/// and a path ending in a separator loses it to the sentence. WPF's text engine predates directional isolates, so
/// only left-to-right marks are used: one after the product name, and one on each side of every format
/// placeholder. Installer arguments are technical values (paths, versions, system names) that read left to right.
/// Authored rows stay free of invisible characters; the marks are added once, when the table is built.
/// </remarks>
internal static partial class InstallerRightToLeftText
{
    /// <summary>Left-to-right mark.</summary>
    internal const char LeftToRightMark = '\u200E';

    /// <summary>The product name as rows write it.</summary>
    internal const string ProductName = "Clash#";

    /// <summary>Prepares one right-to-left text; preparing a prepared text changes nothing.</summary>
    /// <param name="text">Authored text, possibly a composite format.</param>
    /// <returns>The text with the product name and placeholders bound left to right.</returns>
    internal static string Prepare(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
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
                if (builder.Length == 0 || builder[^1] != LeftToRightMark) { builder.Append(LeftToRightMark); }
                builder.Append(placeholder.Value);
                index += placeholder.Length;
                if (index >= text.Length || text[index] != LeftToRightMark) { builder.Append(LeftToRightMark); }
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

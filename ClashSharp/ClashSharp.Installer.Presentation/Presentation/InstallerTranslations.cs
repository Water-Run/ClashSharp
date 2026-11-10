namespace ClashSharp.Installer.Presentation;

/// <summary>
/// Translations of every installer text. Each row is one text: the Simplified Chinese source, then Traditional
/// Chinese, English, Russian, French, German, Persian and Korean, in <see cref="InstallerLanguage"/> order.
/// </summary>
/// <remarks>
/// Rows are split across partial files only to keep each file reviewable. A test checks that every text the
/// installer uses has exactly one row, that every row has all eight columns, and that each translation keeps the
/// source's format placeholders.
/// </remarks>
internal static partial class InstallerTranslations
{
    // Static fields of a partial class are initialized in an unspecified order across its files, so the table is
    // built on first use, after every part has been initialized, never in a field initializer.
    private static readonly Lazy<Dictionary<string, string[]>> LazyTable = new(Build);

    /// <summary>Gets the translations by source text; the array holds the seven non-source languages.</summary>
    internal static IReadOnlyDictionary<string, string[]> Table => LazyTable.Value;

    /// <summary>Gets every row as written, for validation.</summary>
    internal static IEnumerable<string[]> Rows => Part1.Concat(Part2).Concat(Part3).Concat(Part4);

    private static Dictionary<string, string[]> Build()
    {
        Dictionary<string, string[]> table = new(StringComparer.Ordinal);
        foreach (string[] row in Rows)
        {
            if (row.Length != 8) { throw new InvalidOperationException("Installer translation row must have eight columns: " + row[0]); }
            // A duplicate source keeps the first row; the validation test reports it.
            table.TryAdd(row[0], row[1..]);
        }

        return table;
    }
}

using System.Globalization;
using System.Text;

namespace ClashSharp.Installer.Presentation.Tests;

/// <summary>
/// Persian installer text keeps left-to-right fragments in reading order: the product name is bound by a following
/// left-to-right mark and every placeholder is enclosed in marks, while the authored rows and every other language
/// stay exactly as written.
/// </summary>
/// <remarks>
/// The placeholder oracle is formatting itself: a prepared format filled with plain arguments equals the authored
/// format filled with arguments enclosed in marks. Removing every mark gives back the authored text.
/// </remarks>
public sealed class InstallerRightToLeftTextTests
{
    private const char Lrm = InstallerRightToLeftText.LeftToRightMark;

    // Private-use characters that no row contains, so each one found after formatting is an inserted argument.
    private const char FirstSentinel = (char)0xE000;

    private static readonly string[] Fragments =
    [
        "نصب‌کننده", " ", "، ", ".", "«", "»", "(", ")", ":", "Clash#", "Clash", "#", "Windows", "x64",
        "{0}", "{1}", "{0:N0}", "{{", "}}", "{{0}}", "۱۲", "\\", "/",
    ];

    [Theory]
    [InlineData("این حساب Clash# {0} جدیدتر را دارد", "این حساب Clash#\u200E \u200E{0}\u200E جدیدتر را دارد")]
    [InlineData("{0}: محتوای دیگری دارد.", "\u200E{0}\u200E: محتوای دیگری دارد.")]
    [InlineData("Clash# از {0} به {1} به‌روزرسانی می‌شود.", "Clash#\u200E از \u200E{0}\u200E به \u200E{1}\u200E به‌روزرسانی می‌شود.")]
    [InlineData("{{0}} نمونه", "{{0}} نمونه")]
    [InlineData("نصب", "نصب")]
    public void PrepareBindsTheProductNameAndPlaceholders(string authored, string expected)
    {
        Assert.Equal(expected, InstallerRightToLeftText.Prepare(authored));
    }

    [Fact]
    public void PreparedRandomTextKeepsItsContentFormatsLikeMarkedArgumentsAndIsIdempotent()
    {
        Random random = new(20261110);
        for (int sample = 0; sample < 2000; sample++)
        {
            string authored = string.Concat(Enumerable.Range(0, random.Next(0, 12)).Select(_ => Fragments[random.Next(Fragments.Length)]));
            string prepared = InstallerRightToLeftText.Prepare(authored);

            Assert.Equal(prepared, InstallerRightToLeftText.Prepare(prepared));
            Assert.Equal(authored, prepared.Replace(Lrm.ToString(), string.Empty, StringComparison.Ordinal));
            Assert.DoesNotContain(new string(Lrm, 2), prepared, StringComparison.Ordinal);

            string[] arguments = [@"C:\Program Files\", "1.0.0.0", "Windows 11 (x64)"];
            Assert.Equal(TryFormat(authored, arguments), TryFormat(prepared, arguments).Replace(Lrm.ToString(), string.Empty, StringComparison.Ordinal));

            // Formatting is the placeholder parser: every argument it inserts sits between two marks.
            string[] sentinels = [.. Enumerable.Range(0, 3).Select(static index => ((char)(FirstSentinel + index)).ToString())];
            string formatted = TryFormat(prepared, sentinels);
            for (int index = 0; index < formatted.Length; index++)
            {
                if (formatted[index] < FirstSentinel || formatted[index] > FirstSentinel + 2) { continue; }
                Assert.True(index > 0 && formatted[index - 1] == Lrm && index + 1 < formatted.Length && formatted[index + 1] == Lrm, prepared);
            }
        }
    }

    [Fact]
    public void OnlyThePersianColumnIsPreparedAndTheRowsStayAsWritten()
    {
        foreach (string[] row in InstallerTranslations.Rows)
        {
            Assert.DoesNotContain(row, static cell => cell.Any(IsBidiControl));
            foreach (InstallerLanguage language in Enum.GetValues<InstallerLanguage>())
            {
                string expected = language switch
                {
                    InstallerLanguage.SimplifiedChinese => row[0],
                    InstallerLanguage.Persian => InstallerRightToLeftText.Prepare(row[(int)language]),
                    _ => row[(int)language],
                };
                Assert.Equal(expected, InstallerText.Translate(row[0], language));
            }
        }
    }

    [Fact]
    public void PersianShellTextNeverShowsTheProductNameUnbound()
    {
        InstallerText text = new(InstallerLanguage.Persian);
        foreach (string[] row in InstallerTranslations.Rows.Where(static row => row[(int)InstallerLanguage.Persian].Contains("Clash#", StringComparison.Ordinal)))
        {
            string shown = text[row[0]];
            Assert.DoesNotMatch("Clash#(?!\u200E)", shown);
        }
    }

    private static bool IsBidiControl(char value) =>
        value is '\u200E' or '\u200F' or '\u061C' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    private static string TryFormat(string format, object[] arguments)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, arguments);
        }
        catch (FormatException)
        {
            return "<invalid>";
        }
    }
}

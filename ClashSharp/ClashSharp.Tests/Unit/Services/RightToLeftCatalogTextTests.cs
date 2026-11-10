using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Strings;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Checks the bidirectional preparation of right-to-left catalogs against independent oracles.</summary>
/// <remarks>
/// The oracle for placeholders is formatting itself: a prepared format filled with plain arguments must equal the
/// authored format filled with arguments that are each wrapped in a first-strong isolate. The oracle for the
/// product name is that every occurrence is followed by exactly one left-to-right mark. Removing every inserted
/// mark must give back the authored text, so preparation never changes visible content.
/// </remarks>
public sealed class RightToLeftCatalogTextTests
{
    private const char Lrm = RightToLeftCatalogText.LeftToRightMark;
    private const char Fsi = RightToLeftCatalogText.FirstStrongIsolate;
    private const char Pdi = RightToLeftCatalogText.PopDirectionalIsolate;

    private static readonly string[] Fragments =
    [
        "پراکسی", " ", "، ", ".", "«", "»", "(", ")", ":", "Clash#", "Clash", "#", "Windows", "TUN",
        "{0}", "{1}", "{0:N0}", "{1:g}", "{2}", "{{", "}}", "{{0}}", "۱۲", "12", "/", "\\",
    ];

    /// <summary>Verifies the product name binds to a following mark and plain text passes unchanged.</summary>
    [Theory]
    [InlineData("نتایج را در Clash# بررسی کنید.", "نتایج را در Clash#\u200E بررسی کنید.")]
    [InlineData("Clash#", "Clash#\u200E")]
    [InlineData("به Clash# اجازه دهید", "به Clash#\u200E اجازه دهید")]
    [InlineData("«{0}» وارد شد", "«\u2068{0}\u2069» وارد شد")]
    [InlineData("{0:N0} گره و {1} قانون", "\u2068{0:N0}\u2069 گره و \u2068{1}\u2069 قانون")]
    [InlineData("{{0}} نمونه", "{{0}} نمونه")]
    [InlineData("{{{0}}} نمونه", "{{\u2068{0}\u2069}} نمونه")]
    [InlineData("{0,-8} نمونه", "\u2068{0,-8}\u2069 نمونه")]
    [InlineData("پراکسی شفاف", "پراکسی شفاف")]
    public void Prepare_BindsProductNameAndIsolatesPlaceholders(string authored, string expected)
    {
        Assert.Equal(expected, RightToLeftCatalogText.Prepare(authored));
    }

    /// <summary>Verifies preparation over random catalog-like text against the formatting and removal oracles.</summary>
    [Fact]
    public void Prepare_RandomText_KeepsContentIsolatesArgumentsAndIsIdempotent()
    {
        Random random = new(20261110);
        for (int sample = 0; sample < 2000; sample++)
        {
            string authored = string.Concat(Enumerable.Range(0, random.Next(0, 12)).Select(_ => Fragments[random.Next(Fragments.Length)]));
            string prepared = RightToLeftCatalogText.Prepare(authored);

            Assert.Equal(prepared, RightToLeftCatalogText.Prepare(prepared));
            Assert.Equal(authored, RemoveMarks(prepared));
            Assert.Equal(CountOrdinal(authored, "Clash#"), CountOrdinal(prepared, "Clash#" + Lrm));
            Assert.DoesNotContain(Lrm.ToString() + Lrm, prepared, StringComparison.Ordinal);

            string[] arguments = ["Hong Kong (x2)", "۱۲", "a]b"];
            string? expected = TryFormat(authored, arguments.Select(argument => Fsi + argument + Pdi).ToArray());
            // Product-name marks are counted above; the comparison is about placeholders, so they are removed here.
            string? actual = TryFormat(prepared, arguments)?.Replace(Lrm.ToString(), string.Empty, StringComparison.Ordinal);
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Verifies every right-to-left runtime catalog is prepared and no left-to-right catalog carries marks.</summary>
    [Fact]
    public void RuntimeCatalogs_PrepareExactlyTheRightToLeftLanguages()
    {
        IReadOnlyDictionary<AppLanguage, System.Collections.ObjectModel.ReadOnlyDictionary<string, string>> explicitCatalogs =
            LocalizationResources.BuildExplicitTranslations();
        IReadOnlyDictionary<string, string> english = explicitCatalogs[AppLanguage.English];
        List<string> failures = [];
        foreach ((AppLanguage language, System.Collections.ObjectModel.ReadOnlyDictionary<string, string> runtime) in LocalizationResources.Translations)
        {
            bool rightToLeft = LocalizationService.IsRightToLeft(language, CultureInfo.InvariantCulture);
            IReadOnlyDictionary<string, string> authored = explicitCatalogs.TryGetValue(language, out var own) ? own : explicitCatalogs[AppLanguage.SimplifiedChinese];
            foreach ((string key, string value) in runtime)
            {
                string source = authored.TryGetValue(key, out string? text) ? text : english[key];
                string expected = rightToLeft ? RightToLeftCatalogText.Prepare(source) : source;
                if (!string.Equals(expected, value, StringComparison.Ordinal))
                {
                    failures.Add($"{language} {key}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(40)));
        Assert.Contains(Enum.GetValues<AppLanguage>(), language => LocalizationService.IsRightToLeft(language, CultureInfo.InvariantCulture));
    }

    /// <summary>Verifies authored catalogs stay free of invisible bidirectional characters.</summary>
    [Fact]
    public void AuthoredCatalogs_ContainNoBidirectionalControls()
    {
        foreach ((AppLanguage language, System.Collections.ObjectModel.ReadOnlyDictionary<string, string> catalog) in LocalizationResources.BuildExplicitTranslations())
        {
            foreach ((string key, string value) in catalog)
            {
                Assert.False(value.Any(IsBidiControl), $"{language} {key} carries a bidirectional control character.");
            }
        }
    }

    private static bool IsBidiControl(char value) =>
        value is '\u200E' or '\u200F' or '\u061C' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    private static string RemoveMarks(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char value in text)
        {
            if (value is not (Lrm or Fsi or Pdi)) { builder.Append(value); }
        }

        return builder.ToString();
    }

    private static int CountOrdinal(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string? TryFormat(string format, object[] arguments)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, arguments);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

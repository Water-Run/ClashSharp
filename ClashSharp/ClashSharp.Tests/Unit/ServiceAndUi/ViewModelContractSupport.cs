using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using ClashSharp.Model;
using ClashSharp.Strings;
using ClashSharp.Tests.Unit.ViewModel;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>Shared helpers for the language and notification contracts of page view models.</summary>
internal static class ViewModelContractSupport
{
    /// <summary>Gets every language that has a translation table, so a new language is covered automatically.</summary>
    internal static AppLanguage[] AllLanguages { get; } = [.. LocalizationResources.Translations.Keys.Order()];

    internal static IEnumerable<AppLanguage> LanguagePairs(AppLanguage from)
    {
        return AllLanguages.Where(language => language != from);
    }

    /// <summary>Reads every public string property, by name.</summary>
    internal static SortedDictionary<string, string?> ReadStrings(object instance)
    {
        SortedDictionary<string, string?> values = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in instance.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(candidate => candidate.PropertyType == typeof(string) && candidate.GetIndexParameters().Length == 0))
        {
            values[property.Name] = (string?)property.GetValue(instance);
        }

        return values;
    }

    /// <summary>Runs <paramref name="refresh"/> and returns the property names that were announced.</summary>
    internal static HashSet<string> Announced(INotifyPropertyChanged instance, Action refresh)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        PropertyChangedEventHandler handler = (_, args) => names.Add(args.PropertyName ?? string.Empty);
        instance.PropertyChanged += handler;
        try
        {
            refresh();
        }
        finally
        {
            instance.PropertyChanged -= handler;
        }

        return names;
    }

    /// <summary>
    /// A language switch followed by the page refresh must (1) announce every string that changed,
    /// so bound controls cannot keep stale text, and (2) end in exactly the text a view model created
    /// directly in the new language would show.
    /// </summary>
    internal static void AssertSwitchEqualsFreshAndAnnouncesEveryChange(
        string context,
        object switched,
        object fresh,
        Action changeLanguageAndRefresh)
    {
        SortedDictionary<string, string?> before = ReadStrings(switched);

        HashSet<string> announced = Announced((INotifyPropertyChanged)switched, changeLanguageAndRefresh);

        SortedDictionary<string, string?> after = ReadStrings(switched);
        SortedDictionary<string, string?> expected = ReadStrings(fresh);
        foreach ((string name, string? value) in expected)
        {
            Assert.True(
                string.Equals(value, after[name], StringComparison.Ordinal),
                $"{context}: {name} is stale. Expected '{value}', shows '{after[name]}'.");
            if (!string.Equals(before[name], after[name], StringComparison.Ordinal))
            {
                Assert.True(announced.Contains(name), $"{context}: {name} changed but was not announced.");
            }
        }
    }

    internal static string CultureInvariantFormat(string format, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, format, arguments);
}

using System.Text.RegularExpressions;
using System.Windows;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Resource references that WPF resolves late fail silently: a missing dynamic key renders as nothing. This verifies, on the
/// owning STA inside the production application, that every static or dynamic key the installer markup references resolves in
/// the system, light and dark themes.
/// </summary>
internal static partial class InstallerWpfDynamicResourceBehavior
{
    private static readonly string[] MarkupFiles = ["MainWindow.xaml", @"Themes\InstallerTheme.xaml", "App.xaml"];

    internal static void Verify(Application application, string installerSource)
    {
        ArgumentNullException.ThrowIfNull(application);
        string[] keys = MarkupFiles
            .SelectMany(file => ResourceReference().Matches(File.ReadAllText(Path.Combine(installerSource, file))).Select(static match => match.Groups["key"].Value))
            .Where(static key => !key.StartsWith('{'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(keys.Length >= 5, $"Only {keys.Length} resource keys were found.");

#pragma warning disable WPF0001 // Exercise the production Fluent themes on the owning STA.
        ThemeMode previous = application.ThemeMode;
        try
        {
            foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark, ThemeMode.System })
            {
                application.ThemeMode = theme;
                foreach (string key in keys)
                {
                    Assert.True(
                        application.TryFindResource(key) is not null,
                        $"Resource '{key}' does not resolve under the {theme.Value} theme and would render as nothing.");
                }
            }
        }
        finally
        {
            application.ThemeMode = previous;
        }
#pragma warning restore WPF0001
    }

    [GeneratedRegex(@"\{(?:Static|Dynamic)Resource\s+(?<key>\{[^}]*\}|[^\s,}]+)\s*\}")]
    private static partial Regex ResourceReference();
}

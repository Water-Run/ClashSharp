using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>Loads the production dictionaries instead of inferring runtime resource validity from XAML text.</summary>
public sealed class InstallerWpfResourceTests
{
    private static readonly string[] ColorResources =
    [
        "WindowBackground", "WindowForeground", "ButtonBackground",
        "ControlElevationBorderBrush", "TextFillColorSecondaryBrush", "TextFillColorDisabledBrush",
    ];

    [Fact]
    public void ProductionDictionariesMaterializeStylesAndControlsWithoutMissingResources()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                InstallerBackdropBootstrap.Configure();
#pragma warning disable WPF0001 // Match the production application's native Fluent theme selection.
                Application application = new() { ThemeMode = ThemeMode.System, ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning restore WPF0001
                try
                {
                    string source = FindInstallerSource();
                    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                    XElement resources = XDocument.Load(Path.Combine(source, "App.xaml"))
                        .Descendants(presentation + "Application.Resources")
                        .Elements(presentation + "ResourceDictionary").Single();
                    Uri baseUri = new(Path.Combine(source, "App.xaml"));
                    // Attach the base dictionary before parsing dependent source dictionaries,
                    // matching compiled App.xaml's deferred resource lookup.
                    XElement merged = resources.Element(presentation + "ResourceDictionary.MergedDictionaries")!;
                    foreach (XElement entry in merged.Elements(presentation + "ResourceDictionary"))
                    {
                        Uri location = new(baseUri, entry.Attribute("Source")!.Value);
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = location });
                    }
                    merged.Remove();
                    ResourceDictionary inline = (ResourceDictionary)XamlReader.Parse(resources.ToString(), new ParserContext { BaseUri = baseUri });
                    foreach (object key in inline.Keys) { application.Resources.Add(key, inline[key]); }

                    // ResourceDictionary defers parsing. Force every application resource to
                    // materialize so an invalid BasedOn reference cannot hide until window creation.
                    Materialize(application.Resources);
                    foreach (string key in ColorResources)
                    {
                        Assert.NotNull(application.TryFindResource(key));
                    }
                    foreach (Control control in new Control[] { new Button(), new ProgressBar(), new Expander(), new TextBox() })
                    {
                        object key = control is ProgressBar ? "InstallerProgressBarStyle" : control.GetType();
                        control.Style = Assert.IsType<Style>(application.TryFindResource(key));
                        control.Measure(new Size(480, 96));
                        control.Arrange(new Rect(0, 0, 480, 96));
                        control.ApplyTemplate();
                    }
                    Window window = new()
                    {
                        Style = Assert.IsType<Style>(application.TryFindResource("InstallerWindowStyle")),
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        Left = -10000,
                        Top = -10000,
                        Width = 320,
                        Height = 180,
                    };
                    try { window.Show(); }
                    finally { window.Close(); }

                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                    Task behavior = InstallerWpfShellBehavior.VerifyAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    DispatcherFrame frame = new();
                    _ = behavior.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                    Dispatcher.PushFrame(frame);
                    behavior.GetAwaiter().GetResult();
                }
                finally
                {
                    application.Shutdown();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF resource loading did not finish on its owning STA.");
        Assert.Null(failure);
    }

    private static void Materialize(ResourceDictionary dictionary)
    {
        foreach (ResourceDictionary merged in dictionary.MergedDictionaries.ToArray()) { Materialize(merged); }
        foreach (object key in dictionary.Keys.Cast<object>().ToArray())
        {
            _ = dictionary[key];
        }
    }

    private static string FindInstallerSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string source = Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.Installer");
            if (File.Exists(Path.Combine(source, "App.xaml"))) { return source; }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The production Installer XAML was not found.");
    }
}

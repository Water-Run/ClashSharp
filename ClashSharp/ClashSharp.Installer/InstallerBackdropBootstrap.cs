using System.Runtime.InteropServices;

namespace ClashSharp.Installer;

/// <summary>Retains Fluent controls when the current desktop cannot provide a composited window background.</summary>
internal static class InstallerBackdropBootstrap
{
    /// <summary>Runs before WPF caches backdrop support or constructs application resources.</summary>
    internal static void Configure()
    {
        if (!HasDesktopComposition())
        {
            AppContext.SetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", true);
        }
    }

    private static bool HasDesktopComposition()
    {
        if (!Environment.UserInteractive) { return false; }
        try
        {
            return DwmIsCompositionEnabled(out bool enabled) == 0 && enabled;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}

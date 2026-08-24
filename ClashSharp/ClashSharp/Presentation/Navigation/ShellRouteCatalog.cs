using System;

namespace ClashSharp.Presentation.Navigation;

/// <summary>Owns the boundary between XAML/tray tags and typed shell routes.</summary>
internal static class ShellRouteCatalog
{
    public static bool TryParse(string? tag, out ShellRoute route)
    {
        switch (tag)
        {
            case "MasterControl":
                route = ShellRoute.MasterControl;
                return true;
            case "ProxyNodes":
                route = ShellRoute.ProxyNodes;
                return true;
            case "Profiles":
                route = ShellRoute.Profiles;
                return true;
            case "Links":
                route = ShellRoute.Links;
                return true;
            case "Rules":
                route = ShellRoute.Rules;
                return true;
            case "Triggers":
                route = ShellRoute.Triggers;
                return true;
            case "Connections":
                route = ShellRoute.Connections;
                return true;
            case "Statistics":
                route = ShellRoute.Statistics;
                return true;
            case "Logs":
                route = ShellRoute.Logs;
                return true;
            case "About":
                route = ShellRoute.About;
                return true;
            case "Settings":
                route = ShellRoute.Settings;
                return true;
            default:
                route = default;
                return false;
        }
    }

    public static string GetTag(ShellRoute route)
    {
        return route switch
        {
            ShellRoute.MasterControl => "MasterControl",
            ShellRoute.ProxyNodes => "ProxyNodes",
            ShellRoute.Profiles => "Profiles",
            ShellRoute.Links => "Links",
            ShellRoute.Rules => "Rules",
            ShellRoute.Triggers => "Triggers",
            ShellRoute.Connections => "Connections",
            ShellRoute.Statistics => "Statistics",
            ShellRoute.Logs => "Logs",
            ShellRoute.About => "About",
            ShellRoute.Settings => "Settings",
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unsupported shell route."),
        };
    }
}

using System;
using ClashSharp.Presentation.Composition;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Presentation.Navigation;

/// <summary>Creates every navigable page from AppHost-owned, page-specific dependencies.</summary>
internal sealed class ApplicationPageFactory(
    PageCompositionContext context,
    IShellNavigationService navigation) : IPageFactory
{
    public Page Create(ShellRoute route, string? parameter = null)
    {
        return route switch
        {
            ShellRoute.MasterControl => new View.MasterControl(
                MasterControlPageComposition.Create(
                    context,
                    () => navigation.Navigate(ShellRoute.Settings))),
            ShellRoute.ProxyNodes => new View.Proxies(
                ProxiesPageComposition.Create(context)),
            ShellRoute.Profiles => new View.Profiles(
                ProfilesPageComposition.Create(context)),
            ShellRoute.Links => new View.Links(
                LinksPageComposition.Create(context)),
            ShellRoute.Rules => new View.Rules(
                RulesPageComposition.Create(context)),
            ShellRoute.Triggers => new View.Triggers(
                TriggersPageComposition.Create(
                    context,
                    () => navigation.Navigate(ShellRoute.Logs, "Trigger"))),
            ShellRoute.Connections => new View.Connections(
                ConnectionsPageComposition.Create(context)),
            ShellRoute.Statistics => new View.Statistics(
                StatisticsPageComposition.Create(
                    context,
                    () => navigation.Navigate(ShellRoute.Logs))),
            ShellRoute.Logs => new View.Logs(
                LogsPageComposition.Create(
                    context,
                    parameter,
                    () => navigation.GoBack(ShellRoute.Statistics))),
            ShellRoute.About => new View.About(
                AboutPageComposition.Create(context)),
            ShellRoute.Settings => new View.Settings(
                SettingsPageComposition.Create(context)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(route),
                route,
                "Unsupported shell route."),
        };
    }
}

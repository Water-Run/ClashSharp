using System;

namespace ClashSharp.Presentation.Navigation;

/// <summary>Narrow semantic navigation boundary exposed to page composition.</summary>
internal interface IShellNavigationService
{
    event Action<ShellNavigationRequest>? NavigationRequested;

    void Navigate(ShellRoute route, string? parameter = null);

    void GoBack(ShellRoute fallbackRoute);
}

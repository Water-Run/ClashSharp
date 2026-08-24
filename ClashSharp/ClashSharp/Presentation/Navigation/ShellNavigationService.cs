using System;

namespace ClashSharp.Presentation.Navigation;

/// <summary>Window-scoped publisher for typed navigation intents.</summary>
internal sealed class ShellNavigationService : IShellNavigationService, IDisposable
{
    public event Action<ShellNavigationRequest>? NavigationRequested;

    public void Navigate(ShellRoute route, string? parameter = null)
    {
        NavigationRequested?.Invoke(new ShellNavigationRequest(route, parameter, IsBackNavigation: false));
    }

    public void GoBack(ShellRoute fallbackRoute)
    {
        NavigationRequested?.Invoke(new ShellNavigationRequest(fallbackRoute, null, IsBackNavigation: true));
    }

    public void Dispose()
    {
        NavigationRequested = null;
    }
}

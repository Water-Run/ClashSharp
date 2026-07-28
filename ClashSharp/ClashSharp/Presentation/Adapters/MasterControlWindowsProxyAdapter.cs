using System;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="WindowsProxyService"/> to master-control proxy state reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null Windows proxy service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads Windows proxy registry state.
/// </remarks>
internal sealed class MasterControlWindowsProxyAdapter : IMasterControlWindowsProxy
{
    /// <summary>Wrapped Windows proxy service.</summary>
    private readonly WindowsProxyService _windowsProxy;

    /// <summary>Initializes a master-control Windows proxy adapter.</summary>
    /// <param name="windowsProxy">Windows proxy service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="windowsProxy"/> is null.</exception>
    public MasterControlWindowsProxyAdapter(WindowsProxyService windowsProxy)
    {
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
    }

    /// <summary>Gets current Windows system proxy state.</summary>
    /// <returns>Current Windows proxy state.</returns>
    public WindowsProxyState GetCurrentState()
    {
        return _windowsProxy.GetCurrentState();
    }
}

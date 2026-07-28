using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Windows proxy state contract required by <see cref="MasterControlViewModel"/>.</summary>
internal interface IMasterControlWindowsProxy
{
    /// <summary>Gets current Windows system proxy state.</summary>
    WindowsProxyState GetCurrentState();
}

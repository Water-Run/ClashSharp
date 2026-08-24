using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Presentation.Navigation;

/// <summary>Creates WinUI pages for typed shell routes without framework activation.</summary>
internal interface IPageFactory
{
    Page Create(ShellRoute route, string? parameter = null);
}

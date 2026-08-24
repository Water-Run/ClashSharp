namespace ClashSharp.Presentation.Navigation;

/// <summary>One semantic navigation request emitted by a page, tray command, or shell selection.</summary>
internal sealed record ShellNavigationRequest(
    ShellRoute Route,
    string? Parameter,
    bool IsBackNavigation);

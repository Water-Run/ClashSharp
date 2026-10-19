using System;
using Windows.ApplicationModel.DataTransfer;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Copies an explicitly requested private MCP configuration with per-content clipboard restrictions.</summary>
internal static class McpClientClipboard
{
    /// <summary>Excludes the bearer-containing configuration from Windows clipboard history and cloud synchronization.</summary>
    /// <param name="configuration">Private client configuration to copy. Never retained by this adapter.</param>
    /// <returns>Whether Windows accepted the clipboard content; false does not indicate a successful copy.</returns>
    public static bool TryCopy(string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        DataPackage package = new();
        package.SetText(configuration);
        ClipboardContentOptions options = new()
        {
            IsAllowedInHistory = false,
            IsRoamable = false,
        };
        return SystemClipboard.SetContentWithOptions(package, options);
    }
}

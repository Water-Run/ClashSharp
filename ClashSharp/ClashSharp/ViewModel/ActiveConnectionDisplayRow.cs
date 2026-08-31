using System;
using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable display row for one active connection.</summary>
public sealed class ActiveConnectionDisplayRow
{
    /// <summary>Creates sanitized, culture-aware display values for one connection snapshot.</summary>
    /// <param name="connection">Immutable connection data represented by this row.</param>
    /// <param name="displayTextFilter">Boundary filter applied to externally sourced text.</param>
    public ActiveConnectionDisplayRow(ActiveConnection connection, Func<string, string> displayTextFilter)
    {
        ArgumentNullException.ThrowIfNull(displayTextFilter);

        Connection = connection;
        ProcessNameDisplay = displayTextFilter(connection.ProcessName);
        HostDisplay = displayTextFilter(connection.Host);
        RuleDisplay = displayTextFilter(connection.RawRuleDisplay);
        ProxyNameDisplay = displayTextFilter(connection.ProxyName);
        UploadDisplay = FormatByteCount(connection.UploadBytes);
        DownloadDisplay = FormatByteCount(connection.DownloadBytes);
    }

    /// <summary>Gets the immutable connection snapshot represented by this row.</summary>
    public ActiveConnection Connection { get; }

    /// <summary>Gets the sanitized process-name display text.</summary>
    public string ProcessNameDisplay { get; }

    /// <summary>Gets the sanitized destination-host display text.</summary>
    public string HostDisplay { get; }

    /// <summary>Gets the sanitized matched-rule display text.</summary>
    public string RuleDisplay { get; }

    /// <summary>Gets the sanitized selected-proxy display text.</summary>
    public string ProxyNameDisplay { get; }

    /// <summary>Gets the culture-formatted uploaded byte count.</summary>
    public string UploadDisplay { get; }

    /// <summary>Gets the culture-formatted downloaded byte count.</summary>
    public string DownloadDisplay { get; }

    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return value.ToString("N1", CultureInfo.CurrentCulture) + " " + units[unitIndex];
    }
}

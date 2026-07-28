using System;
using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable display row for one active connection.</summary>
public sealed class ActiveConnectionDisplayRow
{
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

    public ActiveConnection Connection { get; }

    public string ProcessNameDisplay { get; }

    public string HostDisplay { get; }

    public string RuleDisplay { get; }

    public string ProxyNameDisplay { get; }

    public string UploadDisplay { get; }

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

using System;
using System.Globalization;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable display row for one active connection.</summary>
public sealed class ActiveConnectionDisplayRow
{
    /// <summary>Creates sanitized, culture-aware display values for one connection snapshot.</summary>
    /// <param name="connection">Immutable connection data represented by this row.</param>
    /// <param name="displayTextFilter">Boundary filter applied to externally sourced text.</param>
    /// <param name="formatter">Optional presentation formatter.</param>
    public ActiveConnectionDisplayRow(ActiveConnection connection, Func<string, string> displayTextFilter, DisplayFormatter? formatter = null)
    {
        ArgumentNullException.ThrowIfNull(displayTextFilter);
        formatter ??= new();

        Connection = connection;
        ProcessNameDisplay = displayTextFilter(connection.ProcessName);
        HostDisplay = displayTextFilter(connection.Host);
        RuleDisplay = displayTextFilter(connection.RawRuleDisplay);
        ProxyNameDisplay = displayTextFilter(connection.ProxyName);
        UploadDisplay = formatter.Bytes(connection.UploadBytes, "N1");
        DownloadDisplay = formatter.Bytes(connection.DownloadBytes, "N1");
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


}

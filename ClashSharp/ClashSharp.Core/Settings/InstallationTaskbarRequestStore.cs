namespace ClashSharp.Settings;

/// <summary>Correlates one-shot taskbar intent without deleting a concurrently replaced request.</summary>
public static class InstallationTaskbarRequestStore
{
    /// <summary>Reads a canonical request which has not already been handled.</summary>
    /// <param name="values">Current package's application-data values.</param>
    public static string? ReadPending(IDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.TryGetValue(InstallationSetupProtocol.TaskbarRequestKey, out object? value) || value is not string text
            || !Guid.TryParseExact(text, "N", out Guid id) || id == Guid.Empty || id.ToString("N") != text) { return null; }
        return values.TryGetValue(InstallationSetupProtocol.TaskbarReceiptKey, out object? handled) && Equals(handled, text) ? null : text;
    }

    /// <summary>Records a matching request as handled, retaining any newer installation intent.</summary>
    /// <param name="values">Current package's application-data values.</param>
    /// <param name="requestId">Canonical identity previously returned by ReadPending.</param>
    public static bool TryConsume(IDictionary<string, object> values, string requestId)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(requestId);
        if (!StringComparer.Ordinal.Equals(ReadPending(values), requestId)) { return false; }
        values[InstallationSetupProtocol.TaskbarReceiptKey] = requestId;
        // Keep the correlated request as a tombstone. Removing it after a separate receipt write
        // could erase a newer request staged by an installer in another process.
        return values.TryGetValue(InstallationSetupProtocol.TaskbarRequestKey, out object? current) && Equals(current, requestId);
    }
}

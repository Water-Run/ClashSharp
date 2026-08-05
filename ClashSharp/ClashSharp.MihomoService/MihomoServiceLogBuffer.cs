using ClashSharp.ServiceProtocol;

namespace ClashSharp.MihomoService;

/// <summary>Thread-safe bounded service-host log ring with secret redaction.</summary>
internal sealed class MihomoServiceLogBuffer
{
    private const int Capacity = 1024;

    private readonly object _syncLock = new();
    private readonly Queue<string> _entries = new(Capacity);
    private readonly HashSet<string> _sensitiveValues = new(StringComparer.OrdinalIgnoreCase);

    public MihomoServiceLogBuffer(MihomoServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sensitiveValues.Add(options.IpcToken);
    }

    /// <summary>
    /// Registers a service-owned capability before it can reach child output or an exception.
    /// Values remain registered for the service process lifetime so delayed output cannot reveal
    /// an authority after its effective configuration has been removed.
    /// </summary>
    internal void RegisterSensitiveValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length is < 16 or > 1024 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Sensitive log values must be bounded opaque text.", nameof(value));
        }

        lock (_syncLock)
        {
            _sensitiveValues.Add(value);
        }
    }

    internal void Append(string category, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(message);
        string redacted = RedactSensitiveValues(message);
        string entry = $"{DateTimeOffset.UtcNow:O} [{category}] {redacted}";
        entry = TruncateAtUtf16Boundary(
            entry,
            MihomoServiceIpcProtocol.MaximumLogEntryCharacters);

        lock (_syncLock)
        {
            if (_entries.Count == Capacity)
            {
                _ = _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }
    }

    internal string RedactSensitiveValues(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] sensitiveValues;
        lock (_syncLock)
        {
            sensitiveValues = _sensitiveValues.ToArray();
        }

        int longestSensitiveValue = sensitiveValues.Length == 0
            ? 0
            : sensitiveValues.Max(static value => value.Length);
        int inputLimit = MihomoServiceIpcProtocol.MaximumLogEntryCharacters
            + longestSensitiveValue;
        string redacted = message.Length > inputLimit
            ? message[..inputLimit]
            : message;
        foreach (string sensitiveValue in sensitiveValues)
        {
            redacted = redacted.Replace(
                sensitiveValue,
                "[redacted]",
                StringComparison.OrdinalIgnoreCase);
        }

        return RepairUnpairedSurrogates(redacted);
    }

    private static string RepairUnpairedSurrogates(string value)
    {
        char[]? repaired = null;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }

            if (!char.IsSurrogate(character))
            {
                continue;
            }

            repaired ??= value.ToCharArray();
            repaired[index] = '\uFFFD';
        }

        return repaired is null ? value : new string(repaired);
    }

    private static string TruncateAtUtf16Boundary(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        int length = maximumCharacters;
        if (length > 0
            && char.IsHighSurrogate(value[length - 1])
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }

    internal IReadOnlyList<string> ReadLatest(int maximumEntries)
    {
        if (maximumEntries is < 1 or > MihomoServiceIpcProtocol.MaximumLogEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        lock (_syncLock)
        {
            List<string> latest = [];
            int aggregateCharacters = 0;
            foreach (string entry in _entries.Reverse())
            {
                if (latest.Count >= maximumEntries
                    || aggregateCharacters + entry.Length
                        > MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters)
                {
                    break;
                }

                latest.Add(entry);
                aggregateCharacters += entry.Length;
            }

            latest.Reverse();
            return latest;
        }
    }
}

using ClashSharp.ServiceProtocol;

namespace ClashSharp.MihomoService;

/// <summary>Independent sequenced ring containing only redacted mihomo child output.</summary>
internal sealed class MihomoRuntimeLogBuffer
{
    private const int Capacity = 1024;
    private readonly object _syncLock = new();
    private readonly Queue<MihomoServiceIpcRuntimeLogEntry> _entries = new(Capacity);
    private readonly MihomoServiceLogBuffer _redactor;
    private long _latestSequence;

    internal MihomoRuntimeLogBuffer(MihomoServiceLogBuffer redactor)
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    internal void Append(string category, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(message);
        string normalized = MihomoServiceIpcProtocol.NormalizeRuntimeLogMessage(
            _redactor.RedactSensitiveValues(message));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_syncLock)
        {
            long sequence = checked(++_latestSequence);
            if (_entries.Count == Capacity)
            {
                _ = _entries.Dequeue();
            }

            _entries.Enqueue(new MihomoServiceIpcRuntimeLogEntry
            {
                Sequence = sequence,
                Level = DetectLevel(category, normalized),
                Message = normalized,
            });
        }
    }

    internal MihomoServiceIpcRuntimeLogSnapshot ReadAfter(long afterSequence, int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);

        if (maximumEntries is < 1 or > MihomoServiceIpcProtocol.MaximumRuntimeLogEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        lock (_syncLock)
        {
            List<MihomoServiceIpcRuntimeLogEntry> selected = [];
            int aggregateCharacters = 0;
            foreach (MihomoServiceIpcRuntimeLogEntry entry in _entries)
            {
                if (entry.Sequence <= afterSequence)
                {
                    continue;
                }

                if (selected.Count >= maximumEntries
                    || aggregateCharacters + entry.Message.Length
                        > MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters)
                {
                    break;
                }

                selected.Add(entry);
                aggregateCharacters += entry.Message.Length;
            }

            return new MihomoServiceIpcRuntimeLogSnapshot
            {
                LatestSequence = _latestSequence,
                Entries = selected,
            };
        }
    }

    private static MihomoServiceIpcRuntimeLogLevel DetectLevel(string category, string message)
    {
        if (message.Contains("error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            return MihomoServiceIpcRuntimeLogLevel.Error;
        }

        if (category.Equals("stderr", StringComparison.OrdinalIgnoreCase)
            || message.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return MihomoServiceIpcRuntimeLogLevel.Warning;
        }

        return message.Contains("debug", StringComparison.OrdinalIgnoreCase)
            ? MihomoServiceIpcRuntimeLogLevel.Debug
            : MihomoServiceIpcRuntimeLogLevel.Information;
    }
}

namespace ClashSharp.ApplicationModel.Diagnostics;

/// <summary>Captures complete diagnostic lines under a fixed character bound.</summary>
public sealed class ConcurrentBoundedTextBuffer
{
    /// <summary>Gets the deterministic line appended when output exceeds the configured bound.</summary>
    public const string TruncationMarker = "[output truncated]";

    private static readonly string TruncationLine = TruncationMarker + Environment.NewLine;

    private readonly object _sync = new();
    private readonly int _maximumCharacters;
    private readonly List<string> _lines = [];

    private int _characterCount;
    private bool _isCompleted;
    private bool _isTruncated;

    /// <summary>Initializes an empty bounded buffer.</summary>
    /// <param name="maximumCharacters">Maximum snapshot characters, including line endings and marker.</param>
    public ConcurrentBoundedTextBuffer(int maximumCharacters)
    {
        if (maximumCharacters < TruncationLine.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters),
                maximumCharacters,
                "The buffer must be large enough for its truncation marker.");
        }

        _maximumCharacters = maximumCharacters;
    }

    /// <summary>Gets whether no later write can be accepted.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_sync)
            {
                return _isCompleted;
            }
        }
    }

    /// <summary>Gets whether at least one line was rejected because the bound was reached.</summary>
    public bool IsTruncated
    {
        get
        {
            lock (_sync)
            {
                return _isTruncated;
            }
        }
    }

    /// <summary>Attempts to append one non-empty line.</summary>
    /// <param name="line">Line content without a line-ending sequence.</param>
    /// <returns>True only when the complete line was accepted.</returns>
    public bool TryAppendLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("Diagnostic input must contain exactly one line.", nameof(line));
        }

        lock (_sync)
        {
            if (_isCompleted || _isTruncated)
            {
                return false;
            }

            int remainingCharacters = _maximumCharacters - _characterCount;
            if (line.Length <= remainingCharacters - Environment.NewLine.Length)
            {
                string rendered = line + Environment.NewLine;
                _lines.Add(rendered);
                _characterCount += rendered.Length;
                return true;
            }

            MarkTruncated();
            return false;
        }
    }

    /// <summary>Prevents all later writes while preserving the current immutable snapshot value.</summary>
    public void Complete()
    {
        lock (_sync)
        {
            _isCompleted = true;
        }
    }

    /// <summary>Returns a stable point-in-time copy containing complete lines only.</summary>
    /// <returns>A string no longer than the configured maximum.</returns>
    public string Snapshot()
    {
        lock (_sync)
        {
            return string.Concat(_lines);
        }
    }

    private void MarkTruncated()
    {
        while (_lines.Count > 0 && _characterCount + TruncationLine.Length > _maximumCharacters)
        {
            string removed = _lines[^1];
            _lines.RemoveAt(_lines.Count - 1);
            _characterCount -= removed.Length;
        }

        _lines.Add(TruncationLine);
        _characterCount += TruncationLine.Length;
        _isTruncated = true;
    }
}

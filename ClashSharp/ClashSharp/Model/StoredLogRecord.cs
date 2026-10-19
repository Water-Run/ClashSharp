namespace ClashSharp.Model;

/// <summary>Detaches one bounded persisted log and its database-local identity from SQLite.</summary>
internal readonly record struct StoredLogRecord(long Id, LogRecord Record, bool IsTruncated);

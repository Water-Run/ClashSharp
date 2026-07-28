namespace ClashSharp.Model;

/// <summary>External process snapshot used by startup conflict checks.</summary>
internal readonly record struct StartupConflictProcess(int ProcessId, string ProcessName);

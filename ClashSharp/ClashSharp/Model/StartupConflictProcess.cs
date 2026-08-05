namespace ClashSharp.Model;

/// <summary>External process identity snapshot used by startup conflict checks and explicit repair.</summary>
internal readonly record struct StartupConflictProcess(
    int ProcessId,
    string ProcessName,
    long StartTimeUtcTicks = 0);

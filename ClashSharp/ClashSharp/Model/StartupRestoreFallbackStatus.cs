namespace ClashSharp.Model;

/// <summary>Current registration state for the startup restore fallback helper.</summary>
public readonly record struct StartupRestoreFallbackStatus(bool IsRegistered, string CommandLine);

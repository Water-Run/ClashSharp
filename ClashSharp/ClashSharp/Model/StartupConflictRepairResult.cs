namespace ClashSharp.Model;

/// <summary>Result returned after attempting to repair a startup conflict.</summary>
internal readonly record struct StartupConflictRepairResult(bool Succeeded, string Message);

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Model;

/// <summary>A detected startup conflict and its repair action.</summary>
internal sealed class StartupConflictIssue
{
    /// <summary>Creates an informational issue that intentionally has no automatic repair.</summary>
    public StartupConflictIssue(
        StartupConflictKind kind,
        string title,
        string description)
        : this(
            kind,
            title,
            description,
            string.Empty,
            static cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new StartupConflictRepairResult(false, string.Empty));
            })
    {
        HasRepairAction = false;
    }

    public StartupConflictIssue(
        StartupConflictKind kind,
        string title,
        string description,
        string repairText,
        Func<CancellationToken, Task<StartupConflictRepairResult>> repairAsync)
    {
        Kind = kind;
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        RepairText = repairText ?? throw new ArgumentNullException(nameof(repairText));
        RepairAsync = repairAsync ?? throw new ArgumentNullException(nameof(repairAsync));
        HasRepairAction = true;
    }

    public StartupConflictKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    public string RepairText { get; }

    public Func<CancellationToken, Task<StartupConflictRepairResult>> RepairAsync { get; }

    /// <summary>Gets whether the UI may offer the explicit repair callback.</summary>
    public bool HasRepairAction { get; }

    /// <summary>Gets or initializes the stable support code for this detected condition.</summary>
    public string DiagnosticCode { get; init; } = string.Empty;
}

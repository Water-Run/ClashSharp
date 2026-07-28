using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Model;

/// <summary>A detected startup conflict and its repair action.</summary>
internal sealed class StartupConflictIssue
{
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
    }

    public StartupConflictKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    public string RepairText { get; }

    public Func<CancellationToken, Task<StartupConflictRepairResult>> RepairAsync { get; }
}

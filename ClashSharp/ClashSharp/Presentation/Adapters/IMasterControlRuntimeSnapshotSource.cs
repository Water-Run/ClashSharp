using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Captures the caller-thread state needed to create one runtime snapshot.</summary>
/// <remarks>
/// Implementations must keep capture lightweight. UI-thread-affine dependencies belong here, while
/// file parsing and SQLite aggregation belong in the returned work item.
/// </remarks>
internal interface IMasterControlRuntimeSnapshotSource
{
    IMasterControlRuntimeSnapshotWork Capture();
}

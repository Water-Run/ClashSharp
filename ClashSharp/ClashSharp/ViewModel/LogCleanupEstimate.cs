namespace ClashSharp.ViewModel;

/// <summary>Estimated impact of a log cleanup operation.</summary>
internal readonly record struct LogCleanupEstimate(
    long EntryCount,
    long EstimatedSizeBytes);

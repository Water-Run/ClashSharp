namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Identifies a stable mutation journal persistence failure.</summary>
public enum MutationJournalStoreError
{
    /// <summary>Stored bytes, hash, or document fields are corrupt.</summary>
    Corrupt,

    /// <summary>The stored schema version is unsupported.</summary>
    UnsupportedSchema,

    /// <summary>The caller attempted to overwrite a different durable generation.</summary>
    ConcurrencyConflict,

    /// <summary>The next journal generation was not exactly one greater than the current generation.</summary>
    InvalidGeneration,

    /// <summary>The recovery root or target path is unsafe.</summary>
    UnsafePath,
}

/// <summary>Represents a typed mutation journal persistence failure.</summary>
public sealed class MutationJournalStoreException : IOException
{
    /// <summary>Initializes a typed store failure.</summary>
    /// <param name="error">Stable failure classification.</param>
    /// <param name="message">Diagnostic message for logs.</param>
    /// <param name="innerException">Optional underlying persistence or parsing failure.</param>
    public MutationJournalStoreException(
        MutationJournalStoreError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public MutationJournalStoreError Error { get; }
}

/// <summary>Persists exactly one replay-capable top-level mutation journal.</summary>
public interface IMutationJournalStore
{
    /// <summary>Loads and validates the current journal, or returns null when none exists.</summary>
    /// <param name="cancellationToken">Cancels read work.</param>
    /// <returns>The validated durable journal snapshot, or null.</returns>
    Task<MutationJournalSnapshot?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Flushes the next journal generation through an atomic replacement.</summary>
    /// <param name="journal">Next immutable journal document.</param>
    /// <param name="expectedCurrentHash">Hash of the generation being replaced, or null for generation one.</param>
    /// <param name="cancellationToken">Cancels work before atomic promotion.</param>
    /// <returns>The validated snapshot that became authoritative.</returns>
    Task<MutationJournalSnapshot> SaveAsync(
        MutationJournal journal,
        string? expectedCurrentHash,
        CancellationToken cancellationToken);

    /// <summary>Deletes the journal only when operation identity and latest hash still match.</summary>
    /// <param name="operationId">Expected current operation identifier.</param>
    /// <param name="expectedCurrentHash">Expected latest durable content hash.</param>
    /// <param name="cancellationToken">Cancels work before deletion.</param>
    Task DeleteAsync(Guid operationId, string expectedCurrentHash, CancellationToken cancellationToken);
}

namespace ClashSharp.ApplicationModel.Data;

/// <summary>Identifies a stable data-generation manifest persistence failure.</summary>
public enum DataGenerationStoreError
{
    /// <summary>Stored bytes, payload fields, or hashes are corrupt.</summary>
    Corrupt,

    /// <summary>The stored envelope or payload schema is unsupported.</summary>
    UnsupportedSchema,

    /// <summary>The caller attempted to replace a different durable manifest.</summary>
    ConcurrencyConflict,

    /// <summary>A generation number or manifest revision violated monotonic sequencing.</summary>
    InvalidGeneration,

    /// <summary>A generation identity or immutable root was already allocated.</summary>
    DuplicateGeneration,

    /// <summary>A descriptor or persisted manifest field is invalid.</summary>
    InvalidDescriptor,

    /// <summary>A supplied expected hash is not canonical SHA-256 text.</summary>
    InvalidHash,

    /// <summary>A root, generation, manifest, or staging path is unsafe.</summary>
    UnsafePath,

    /// <summary>The filesystem could not complete the requested operation.</summary>
    Unavailable,
}

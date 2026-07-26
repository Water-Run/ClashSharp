namespace ClashSharp.ApplicationModel.Data;

/// <summary>Represents a typed data-generation manifest persistence failure.</summary>
public sealed class DataGenerationStoreException : IOException
{
    /// <summary>Initializes a typed store failure.</summary>
    /// <param name="error">Stable failure classification.</param>
    /// <param name="message">Diagnostic message for logs.</param>
    /// <param name="innerException">Optional underlying persistence or parsing failure.</param>
    public DataGenerationStoreException(
        DataGenerationStoreError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public DataGenerationStoreError Error { get; }
}

namespace ClashSharp.ApplicationModel.Data;

/// <summary>Represents a typed generation-manager admission or transition failure.</summary>
public sealed class DataGenerationManagerException : InvalidOperationException
{
    /// <summary>Initializes a typed manager failure.</summary>
    /// <param name="error">Stable failure classification.</param>
    /// <param name="message">Diagnostic message for logs.</param>
    /// <param name="innerException">Optional underlying scope-lifetime failure.</param>
    public DataGenerationManagerException(
        DataGenerationManagerError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public DataGenerationManagerError Error { get; }
}

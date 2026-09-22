namespace ClashSharp.ApplicationModel.Security;

/// <summary>Reports an unavailable credential using a stable code without carrying private storage values.</summary>
public sealed class ControllerCredentialException : InvalidOperationException
{
    /// <summary>Creates a value-free credential failure.</summary>
    /// <param name="code">Stable diagnostic code.</param>
    public ControllerCredentialException(string code) : base(code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Gets the stable failure code.</summary>
    public string Code { get; }
}

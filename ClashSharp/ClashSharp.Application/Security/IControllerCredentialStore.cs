namespace ClashSharp.ApplicationModel.Security;

/// <summary>Persists exactly one controller credential outside the preferences authority.</summary>
public interface IControllerCredentialStore
{
    /// <summary>Reads the credential slot without enumerating user preferences.</summary>
    /// <param name="secret">Stored string, or null when absent or present with an invalid storage type.</param>
    /// <returns>Whether the slot exists; a present invalid value remains distinct from absence.</returns>
    bool TryRead(out string? secret);

    /// <summary>Writes one canonical private credential; return alone does not prove persistence.</summary>
    /// <param name="secret">Canonical lowercase hexadecimal credential.</param>
    void Write(string secret);

    /// <summary>Removes only the credential slot; callers independently verify absence.</summary>
    void Delete();
}

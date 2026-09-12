namespace ClashSharp.Security;

/// <summary>Defines the private App-owned controller credential independently of user preferences.</summary>
public static class ControllerCredentialPolicy
{
    /// <summary>Checks the canonical 256-bit lowercase hexadecimal representation.</summary>
    /// <param name="secret">Untrusted persisted credential; never included in diagnostics.</param>
    public static bool IsValid(string? secret)
    {
        if (secret is not { Length: 64 }) { return false; }
        foreach (char character in secret)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) { return false; }
        }

        return true;
    }
}

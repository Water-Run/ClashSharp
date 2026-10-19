using System.Text.RegularExpressions;

namespace ClashSharp.Diagnostics;

/// <summary>Bounds persisted diagnostics and removes known credential fields, URIs, and user-profile names.</summary>
/// <remarks>
/// Callers must still avoid logging private configuration or credential objects. This boundary
/// limits accidental disclosure from upstream error text; it is not a general-purpose data classifier.
/// </remarks>
public static partial class PersistedLogText
{
    private const string Redacted = "[redacted]";
    private const string CredentialNames = "proxy-authorization|authorization|set-cookie|cookie|client[-_]?secret|access[-_]?token|refresh[-_]?token|id[-_]?token|api[-_]?key|private[-_]?key(?:[-_]?passphrase)?|pre[-_]?shared[-_]?key|shared[-_]?key|primary[-_]?key|ech[-_]?key|psk|auth(?:[-_]?(?:str|string))?|authentication|obfs[-_]?password|password|passwd|pwd|passphrase|secret|token|uuid";

    /// <summary>Returns control-free diagnostic text with bounded processing and storage cost.</summary>
    /// <param name="text">Text to redact before persistence. Must not be null.</param>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int length = Math.Min(text.Length, RuntimeLogText.MaximumCharacters);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
        {
            length--;
        }
        string bounded = text[..length];
        try
        {
            // Keep boundaries until complete quoted, structured and continued values are removed.
            bounded = RedactPrivateKeyBlocks(bounded);
            bounded = RedactCredentialFields(bounded);
            bounded = CredentialElement().Replace(bounded, static match => match.Groups[1].Value + Redacted);
            bounded = PrivateUri().Replace(bounded, "[redacted uri]");
            bounded = UserProfileDirectory().Replace(bounded, "%USERPROFILE%");
            return RuntimeLogText.Normalize(bounded);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[redacted: diagnostic processing limit]";
        }
    }

    [GeneratedRegex("((?:\\b(?<key>" + CredentialNames + ")|(?:^|[^\\p{L}\\p{N}_-])[\"']?(?<key>key))[\"']?[ \\t]*(?<separator>[:=])[ \\t]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex CredentialFieldPrefix();

    [GeneratedRegex("""-----BEGIN (?<label>(?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?|ECH KEYS)-----""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex PrivateKeyBlockStart();

    // XML fragments in diagnostic text need not form a document. Once a known secret
    // element starts, discard the bounded tail rather than expose nested/CDATA content.
    [GeneratedRegex("(<(?:" + CredentialNames + "|key)\\b[^>]*>)[\\s\\S]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex CredentialElement();

    [GeneratedRegex("""\b(?:https?|socks5h?|socks4a?|ssr?|vmess|vless|trojan|hysteria2?|hy2|tuic)://[^\s<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex PrivateUri();

    [GeneratedRegex("""\b[A-Z]:[\\/]Users[\\/][^\\/:*?"<>|\r\n]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex UserProfileDirectory();
}

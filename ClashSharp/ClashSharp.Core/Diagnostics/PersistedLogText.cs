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

    /// <summary>Returns control-free diagnostic text with bounded processing and storage cost.</summary>
    /// <param name="text">Text to redact before persistence. Must not be null.</param>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string bounded = text.Length > RuntimeLogText.MaximumCharacters
            ? text[..RuntimeLogText.MaximumCharacters]
            : text;
        try
        {
            // Retain line boundaries until header values have been removed in their entirety.
            bounded = CredentialHeader().Replace(bounded, static match => match.Groups[1].Value + Redacted);
            bounded = CredentialField().Replace(bounded, static match => match.Groups[1].Value + Redacted);
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

    [GeneratedRegex("""(\b(?:proxy-authorization|authorization|set-cookie|cookie)["']?\s*[:=]\s*)[^\r\n]*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex CredentialHeader();

    [GeneratedRegex("""(\b(?:client[-_]?secret|access[-_]?token|refresh[-_]?token|id[-_]?token|api[-_]?key|password|passwd|pwd|secret|token|uuid)["']?\s*[:=]\s*)(?:\[redacted\]|"[^"]*(?:"|$)|'[^']*(?:'|$)|[^\s,;}\]]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex CredentialField();

    [GeneratedRegex("""(<(?:password|passwd|secret|token|uuid)\b[^>]*>)[^<]*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex CredentialElement();

    [GeneratedRegex("""\b(?:https?|socks5h?|socks4a?|ssr?|vmess|vless|trojan|hysteria2?|hy2|tuic)://[^\s<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex PrivateUri();

    [GeneratedRegex("""\b[A-Z]:[\\/]Users[\\/][^\\/:*?"<>|\r\n]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 250)]
    private static partial Regex UserProfileDirectory();
}

using System.Text;
using System.Text.RegularExpressions;

namespace ClashSharp.Diagnostics;

public static partial class PersistedLogText
{
    /// <summary>Removes complete known secret values from already bounded diagnostic text.</summary>
    private static string RedactCredentialFields(string text)
    {
        Match match = CredentialFieldPrefix().Match(text);
        if (!match.Success) { return text; }
        StringBuilder result = new(text.Length);
        int consumed = 0;
        while (match.Success)
        {
            int valueStart = match.Index + match.Length;
            int end = FindCredentialValueEnd(text, valueStart, match.Groups["key"].Index,
                match.Groups["key"].Value, match.Groups["separator"].Value[0]);
            result.Append(text, consumed, match.Index - consumed).Append(match.Value).Append(Redacted);
            consumed = end;
            match = CredentialFieldPrefix().Match(text, end);
        }
        return result.Append(text, consumed, text.Length - consumed).ToString();
    }

    /// <summary>Matches the exact private PEM/ECH end label; incomplete blocks consume the bounded tail.</summary>
    private static string RedactPrivateKeyBlocks(string text)
    {
        Match match = PrivateKeyBlockStart().Match(text);
        if (!match.Success) { return text; }
        StringBuilder result = new(text.Length);
        int consumed = 0;
        while (match.Success)
        {
            string marker = "-----END " + match.Groups["label"].Value + "-----";
            int closing = text.IndexOf(marker, match.Index + match.Length, StringComparison.OrdinalIgnoreCase);
            int end = closing < 0 ? text.Length : closing + marker.Length;
            result.Append(text, consumed, match.Index - consumed).Append(Redacted);
            consumed = end;
            match = PrivateKeyBlockStart().Match(text, end);
        }
        return result.Append(text, consumed, text.Length - consumed).ToString();
    }

    private static int FindCredentialValueEnd(string text, int start, int keyIndex, string key, char separator)
    {
        if (start == text.Length) { return start; }
        int end = text[start] switch
        {
            '"' or '\'' => FindQuotedEnd(text, start),
            '[' or '{' => FindStructuredEnd(text, start),
            _ => separator == '=' && text[start] is not ('\r' or '\n') && !IsCredentialHeader(key)
                ? FindAssignmentTokenEnd(text, start) : FindContinuedLineEnd(text, start, keyIndex),
        };
        if (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (',' or ';' or '}' or ']'))
        {
            return FindContinuedLineEnd(text, end, keyIndex);
        }
        return end;
    }

    private static bool IsCredentialHeader(string key) => key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
        || key.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
        || key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>Preserves the existing single-token assignment-log contract; values containing spaces must be quoted.</summary>
    private static int FindAssignmentTokenEnd(string text, int start)
    {
        int cursor = start;
        while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] is not (',' or ';' or '}' or ']')) { cursor++; }
        return cursor;
    }

    /// <summary>Accepts diagnostic backslash escaping and YAML doubled single quotes without exposing a suffix.</summary>
    private static int FindQuotedEnd(string text, int start)
    {
        char quote = text[start];
        int cursor = start + 1;
        while (cursor < text.Length)
        {
            char character = text[cursor++];
            if (character == '\\') { if (cursor < text.Length) { cursor++; } continue; }
            if (character != quote) { continue; }
            if (quote == '\'' && cursor < text.Length && text[cursor] == '\'') { cursor++; continue; }
            return cursor;
        }
        return text.Length;
    }

    /// <summary>Bounds nesting without recursion; malformed/truncated containers discard the remaining text.</summary>
    private static int FindStructuredEnd(string text, int start)
    {
        Span<char> expected = stackalloc char[128];
        int depth = 0;
        int cursor = start;
        while (cursor < text.Length)
        {
            char character = text[cursor];
            if (character is '"' or '\'') { cursor = FindQuotedEnd(text, cursor); continue; }
            cursor++;
            if (character is '[' or '{')
            {
                if (depth == expected.Length) { return text.Length; }
                expected[depth++] = character == '[' ? ']' : '}';
            }
            else if (character is ']' or '}')
            {
                if (depth == 0 || expected[--depth] != character) { return text.Length; }
                if (depth == 0) { return cursor; }
            }
        }
        return text.Length;
    }

    /// <summary>
    /// Removes unquoted line values and their indented continuation, including YAML blocks/lists.
    /// Extra same-line context may be discarded because an unquoted credential can itself contain spaces.
    /// </summary>
    private static int FindContinuedLineEnd(string text, int start, int keyIndex)
    {
        int keyLine = keyIndex == 0 ? 0 : text.LastIndexOf('\n', keyIndex - 1) + 1;
        int indentation = 0;
        while (keyLine + indentation < text.Length && text[keyLine + indentation] is ' ' or '\t') { indentation++; }
        bool indentlessList = text[start] is '\r' or '\n';
        int end = FindLineEnd(text, start);
        int next = AfterLineEnding(text, end);
        while (next < text.Length)
        {
            int lineEnd = FindLineEnd(text, next);
            int content = next;
            while (content < lineEnd && text[content] is ' ' or '\t') { content++; }
            bool blank = content == lineEnd;
            bool list = indentlessList && content - next == indentation && content < lineEnd && text[content] == '-'
                && (content + 1 == lineEnd || text[content + 1] is ' ' or '\t');
            if (!blank && content - next <= indentation && !list) { break; }
            end = lineEnd;
            next = AfterLineEnding(text, end);
        }
        return end;
    }

    private static int FindLineEnd(string text, int start)
    {
        int cursor = start;
        while (cursor < text.Length && text[cursor] is not ('\r' or '\n')) { cursor++; }
        return cursor;
    }

    private static int AfterLineEnding(string text, int end)
    {
        if (end < text.Length && text[end] == '\r') { end++; }
        if (end < text.Length && text[end] == '\n') { end++; }
        return end;
    }
}

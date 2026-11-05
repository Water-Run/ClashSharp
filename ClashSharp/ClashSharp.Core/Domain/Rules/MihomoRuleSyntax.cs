namespace ClashSharp.Model.Rules;

/// <summary>Parses the rule's outer fields while retaining nested conditions and regular-expression commas.</summary>
public static class MihomoRuleSyntax
{
    /// <summary>Separates the type, complete payload and action without changing the original rule.</summary>
    /// <param name="text">One native rule scalar.</param>
    /// <param name="rule">Parsed fields when the outer delimiters are unambiguous.</param>
    /// <returns>Whether outer fields are unambiguous and logical conditions have balanced delimiters.</returns>
    public static bool TryParse(string text, out ParsedMihomoRule rule)
    {
        ArgumentNullException.ThrowIfNull(text);
        rule = default;
        int firstComma = text.IndexOf(',');
        if (firstComma < 1) { return false; }
        string type = text[..firstComma].Trim();
        bool logical = type.ToUpperInvariant() is "AND" or "OR" or "NOT" or "SUB-RULE";
        List<int> commas = [firstComma];
        Stack<char> expected = new();
        bool escaped = false;
        for (int index = firstComma + 1; index < text.Length; index++)
        {
            char character = text[index];
            // Ordinary payloads are opaque: parentheses in a process path need not be balanced.
            if (!logical) { if (character == ',') { commas.Add(index); } continue; }
            if (escaped) { escaped = false; continue; }
            if (character == '\\') { escaped = true; continue; }
            if (expected.TryPeek(out char closing) && closing == ']')
            {
                if (character == ']') { expected.Pop(); }
                continue;
            }
            if (character is '(' or '[' or '{')
            {
                expected.Push(character switch { '(' => ')', '[' => ']', _ => '}' });
            }
            else if (character is ')' or ']' or '}')
            {
                if (!expected.TryPop(out char end) || end != character) { return false; }
            }
            else if (character == ',' && expected.Count == 0) { commas.Add(index); }
        }
        if (expected.Count != 0 || commas.Count == 0) { return false; }
        int last = commas.Count - 1;
        int endIndex = text.Length;
        while (last > 0 && text[(commas[last] + 1)..endIndex].Trim() is "no-resolve" or "src")
        {
            endIndex = commas[last--];
        }
        string action = text[(commas[last] + 1)..endIndex].Trim();
        if (type.Length == 0 || action.Length == 0) { return false; }
        string payload = last == 0 ? "*" : text[(commas[0] + 1)..commas[last]].Trim();
        if (last == 0 && !type.Equals("MATCH", StringComparison.OrdinalIgnoreCase)) { return false; }
        rule = new(type, payload, action);
        return true;
    }
}

/// <summary>Contains the complete outer fields of a native rule.</summary>
/// <param name="Type">Native rule type.</param>
/// <param name="Payload">Complete condition or expression; an asterisk for MATCH.</param>
/// <param name="Action">Routing action or sub-rule name.</param>
public readonly record struct ParsedMihomoRule(string Type, string Payload, string Action);

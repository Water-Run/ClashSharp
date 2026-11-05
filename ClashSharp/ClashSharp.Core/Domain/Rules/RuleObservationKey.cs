namespace ClashSharp.Model.Rules;

/// <summary>Identifies the condition exposed by native connection observations, independently of the selected proxy chain.</summary>
public static class RuleObservationKey
{
    /// <summary>Normalizes native PascalCase and YAML hyphenated type names into the same condition key.</summary>
    /// <param name="type">Native or YAML rule type.</param>
    /// <param name="payload">Complete rule condition.</param>
    /// <returns>A stable condition key; MATCH has no payload.</returns>
    public static string Create(string type, string payload)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payload);
        return CreateCore(type, payload, 0);
    }

    private static string CreateCore(string type, string payload, int depth)
    {
        string normalized = type.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        normalized = normalized switch { "SUBRULES" => "SUBRULE", "IPCIDR6" => "IPCIDR", "SRCIPCIDR6" => "SRCIPCIDR", _ => normalized };
        if (normalized == "MATCH") { return normalized; }
        string condition = payload.Trim();
        if (normalized is "NETWORK" or "DOMAIN" or "DOMAINSUFFIX" or "DOMAINKEYWORD" or "DOMAINWILDCARD") { condition = condition.ToLowerInvariant(); }
        if (depth < 32 && normalized is "AND" or "OR" or "NOT" or "SUBRULE"
            && TryLogicPayload(normalized, condition, depth, out string logical)) { condition = logical; }
        return normalized + "," + condition;
    }

    // Native logic Payload() uses PascalCase types and &&/||/! instead of YAML's comma-separated clauses.
    // Canonicalize both representations as length-prefixed child conditions; never infer an action from a proxy chain.
    private static bool TryLogicPayload(string type, string payload, int depth, out string canonical)
    {
        canonical = string.Empty;
        if (payload.Length < 2 || payload[0] != '(' || payload[^1] != ')') { return false; }
        string body = payload[1..^1].Trim();
        if (type == "NOT" && body.StartsWith('!')) { body = body[1..].Trim(); }
        if (type == "SUBRULE")
        {
            if (!TryChild(body, depth, out string child)) { return false; }
            canonical = EncodeChild(child);
            return true;
        }
        List<string> children = [];
        int cursor = 0;
        while (cursor < body.Length)
        {
            while (cursor < body.Length && char.IsWhiteSpace(body[cursor])) { cursor++; }
            if (cursor == body.Length || body[cursor++] != '(') { return false; }
            int start = cursor;
            int nesting = 1;
            while (cursor < body.Length && nesting != 0)
            {
                if (body[cursor] == '(') { nesting++; }
                else if (body[cursor] == ')') { nesting--; }
                cursor++;
            }
            if (nesting != 0 || !TryChild(body[start..(cursor - 1)], depth, out string child)) { return false; }
            children.Add(EncodeChild(child));
            while (cursor < body.Length && char.IsWhiteSpace(body[cursor])) { cursor++; }
            if (cursor == body.Length) { break; }
            if (body[cursor] == ',') { cursor++; }
            else if (type is "AND" or "OR" && body.AsSpan(cursor).StartsWith(type == "AND" ? "&&" : "||", StringComparison.Ordinal)) { cursor += 2; }
            else { return false; }
        }
        if (children.Count == 0 || type == "NOT" && children.Count != 1) { return false; }
        canonical = string.Concat(children);
        return true;
    }

    private static bool TryChild(string text, int depth, out string key)
    {
        int comma = text.IndexOf(',');
        key = comma < 1 ? string.Empty : CreateCore(text[..comma], text[(comma + 1)..], depth + 1);
        return comma >= 1;
    }

    private static string EncodeChild(string key) => FormattableString.Invariant($"{key.Length}:{key}");
}

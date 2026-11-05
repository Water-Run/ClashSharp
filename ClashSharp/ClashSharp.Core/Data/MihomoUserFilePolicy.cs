namespace ClashSharp.Data;

/// <summary>Classifies portable mihomo inputs without carrying live controller state or private transaction files.</summary>
public static class MihomoUserFilePolicy
{
    /// <summary>Returns whether a canonical relative file or directory below the mihomo root is user data.</summary>
    /// <param name="relativePath">A path relative to one generation's mihomo directory.</param>
    /// <returns>Whether migration and backup may preserve this input.</returns>
    public static bool IsPortableRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string normalized = relativePath.Replace('\\', '/');
        string[] parts = normalized.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':'))
            || parts[0].Equals("runtime-generations", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string file = parts[^1];
        if (file.StartsWith("config.yaml.runtime-staging.", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("config.yaml.runtime-backup.", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("config.yaml.staging.", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("config.yaml.backup.", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("config.yaml.restore.", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("config.runtime-state.json.tmp.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Length != 1 || !(file.Equals("config.yaml", StringComparison.OrdinalIgnoreCase)
            || file.Equals("config.runtime-state.json", StringComparison.OrdinalIgnoreCase)
            || file.Equals("cache.db", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("cache.db-", StringComparison.OrdinalIgnoreCase));
    }
}

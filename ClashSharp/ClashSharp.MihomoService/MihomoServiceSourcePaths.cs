namespace ClashSharp.MihomoService;

/// <summary>Resolves configuration and provider inputs from the same opaque data-generation identity.</summary>
internal static class MihomoServiceSourcePaths
{
    internal static string ResolveConfiguration(string installedConfigurationPath, Guid? dataGenerationId)
    {
        if (dataGenerationId == Guid.Empty) { throw new InvalidDataException("An empty data generation cannot own service inputs."); }
        string source = installedConfigurationPath;
        if (dataGenerationId is Guid identity)
        {
            string? mihomoDirectory = Path.GetDirectoryName(installedConfigurationPath);
            if (!string.Equals(Path.GetFileName(installedConfigurationPath), "config.yaml", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(mihomoDirectory), "mihomo", StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(mihomoDirectory) is not string applicationRoot)
            {
                throw new InvalidDataException("The installed service configuration has no canonical application data root.");
            }
            source = Path.Combine(applicationRoot, "Data", "v1", "generations", identity.ToString("N"), "mihomo", "config.yaml");
        }
        // Inspect the complete source chain; requests never carry a privileged filesystem path.
        for (string? current = source; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A service configuration source traverses a reparse point.");
            }
        }
        return source;
    }
}

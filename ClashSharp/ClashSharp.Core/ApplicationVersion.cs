namespace ClashSharp;

/// <summary>Provides the product version stamped by the shared repository build properties.</summary>
public static class ApplicationVersion
{
    /// <summary>Gets the three-component version displayed by application pages.</summary>
    public static string Current { get; } = typeof(ApplicationVersion).Assembly.GetName().Version?.ToString(3)
        ?? "1.0.0";
}

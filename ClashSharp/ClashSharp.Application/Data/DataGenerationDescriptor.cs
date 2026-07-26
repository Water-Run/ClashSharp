namespace ClashSharp.ApplicationModel.Data;

/// <summary>Identifies one immutable on-disk data generation.</summary>
public sealed class DataGenerationDescriptor
{
    /// <summary>Initializes a validated generation descriptor.</summary>
    /// <param name="generationId">Stable nonempty generation identity.</param>
    /// <param name="generationNumber">Positive generation sequence number.</param>
    /// <param name="rootPath">Absolute canonical root for this generation.</param>
    public DataGenerationDescriptor(
        Guid generationId,
        long generationNumber,
        string rootPath)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("The generation identifier cannot be empty.", nameof(generationId));
        }

        if (generationNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generationNumber),
                generationNumber,
                "The generation number must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The generation root must be absolute.", nameof(rootPath));
        }

        GenerationId = generationId;
        GenerationNumber = generationNumber;
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    /// <summary>Gets the stable generation identity.</summary>
    public Guid GenerationId { get; }

    /// <summary>Gets the positive generation sequence number.</summary>
    public long GenerationNumber { get; }

    /// <summary>Gets the normalized absolute generation root.</summary>
    public string RootPath { get; }

    /// <summary>Determines whether another descriptor identifies the same generation and location.</summary>
    /// <param name="other">Descriptor to compare.</param>
    /// <returns>True when identity, number, and normalized root all match.</returns>
    public bool IsSameGeneration(DataGenerationDescriptor? other)
    {
        return other is not null
            && GenerationId == other.GenerationId
            && GenerationNumber == other.GenerationNumber
            && string.Equals(RootPath, other.RootPath, GetPathComparison());
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}

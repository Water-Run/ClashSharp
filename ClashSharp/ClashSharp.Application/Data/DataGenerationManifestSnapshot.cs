namespace ClashSharp.ApplicationModel.Data;

/// <summary>Represents one verified durable current-generation manifest.</summary>
public sealed class DataGenerationManifestSnapshot
{
    /// <summary>Initializes a verified manifest snapshot.</summary>
    /// <param name="descriptor">Current generation descriptor.</param>
    /// <param name="manifestRevision">Positive durable pointer revision.</param>
    /// <param name="highestGenerationNumber">Highest generation number ever allocated.</param>
    /// <param name="contentHash">Canonical lowercase SHA-256 hash of the manifest payload.</param>
    public DataGenerationManifestSnapshot(
        DataGenerationDescriptor descriptor,
        long manifestRevision,
        long highestGenerationNumber,
        string contentHash)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (manifestRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(manifestRevision),
                manifestRevision,
                "The manifest revision must be positive.");
        }

        if (highestGenerationNumber < descriptor.GenerationNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(highestGenerationNumber),
                highestGenerationNumber,
                "The generation high-water mark cannot precede the current generation.");
        }

        if (!IsCanonicalContentHash(contentHash))
        {
            throw new ArgumentException(
                "The content hash must be a canonical lowercase SHA-256 value.",
                nameof(contentHash));
        }

        Descriptor = descriptor;
        ManifestRevision = manifestRevision;
        HighestGenerationNumber = highestGenerationNumber;
        ContentHash = contentHash;
    }

    /// <summary>Gets the current immutable generation descriptor.</summary>
    public DataGenerationDescriptor Descriptor { get; }

    /// <summary>Gets the monotonically increasing durable pointer revision.</summary>
    public long ManifestRevision { get; }

    /// <summary>Gets the highest generation number allocated, including rolled-back candidates.</summary>
    public long HighestGenerationNumber { get; }

    /// <summary>Gets the canonical lowercase SHA-256 hash of the manifest payload.</summary>
    public string ContentHash { get; }

    /// <summary>Determines whether text is a canonical lowercase SHA-256 value.</summary>
    /// <param name="value">Hash text to validate.</param>
    /// <returns>True when the value contains exactly 64 lowercase hexadecimal characters.</returns>
    public static bool IsCanonicalContentHash(string? value)
    {
        return value is { Length: 64 }
            && value.All(static character =>
                character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
    }
}

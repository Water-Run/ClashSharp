namespace ClashSharp.Installer.Payloads;

/// <summary>Central fail-closed size and traversal budgets for one installer release.</summary>
public static class InstallerPayloadBudgets
{
    /// <summary>Maximum encoded embedded manifest size.</summary>
    public const int MaximumManifestBytes = 64 * 1024;

    /// <summary>Maximum exact sibling payload file count.</summary>
    public const int MaximumFileCount = 64;

    /// <summary>Maximum number of implied sibling payload directories.</summary>
    public const int MaximumDirectoryCount = 2;

    /// <summary>Maximum directory depth below the sibling payload root.</summary>
    public const int MaximumDirectoryDepth = 2;

    /// <summary>Maximum canonical relative path length.</summary>
    public const int MaximumRelativePathCharacters = 240;

    /// <summary>Maximum length of any one trusted sibling file.</summary>
    public const long MaximumFileBytes = 512L * 1024 * 1024;

    /// <summary>Maximum combined length of the exact sibling payload.</summary>
    public const long MaximumPayloadBytes = 1024L * 1024 * 1024;

    /// <summary>Maximum packaged certificate length.</summary>
    public const long MaximumCertificateBytes = 1024L * 1024;

    /// <summary>Maximum payload provenance document length.</summary>
    public const long MaximumProvenanceBytes = 64L * 1024;

    /// <summary>Maximum combined uncompressed MSIX entry length.</summary>
    public const long MaximumExpandedPackageBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Maximum central-directory entry count in one trusted MSIX.</summary>
    public const int MaximumPackageArchiveEntries = 4_096;

    /// <summary>Maximum trusted GeoData asset length inside the MSIX.</summary>
    public const long MaximumGeoDataAssetBytes = 256L * 1024 * 1024;

    /// <summary>Exact number of machine-scope files trusted inside the primary MSIX.</summary>
    public const int MachinePayloadFileCount = 7;

    /// <summary>Maximum combined uncompressed length of the trusted machine payload.</summary>
    public const long MaximumMachinePayloadBytes = 1024L * 1024 * 1024;

    /// <summary>Maximum GeoData manifest length inside the MSIX.</summary>
    public const long MaximumGeoDataManifestBytes = 64L * 1024;

    /// <summary>Maximum AppxManifest.xml length inside the MSIX.</summary>
    public const long MaximumAppxManifestBytes = 1024L * 1024;

    /// <summary>Maximum AppxBlockMap.xml length inside the MSIX.</summary>
    public const long MaximumAppxBlockMapBytes = 16L * 1024 * 1024;

    /// <summary>Maximum AppxSignature.p7x length inside the MSIX.</summary>
    public const long MaximumAppxSignatureBytes = 16L * 1024 * 1024;
}

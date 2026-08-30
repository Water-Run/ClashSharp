namespace ClashSharp.Installer.Payloads;

/// <summary>Exact semantic role of one sibling release payload file.</summary>
public enum InstallerPayloadFileRole
{
    /// <summary>The one primary ClashSharp MSIX.</summary>
    PrimaryPackage = 0,

    /// <summary>The one DER package-signing certificate.</summary>
    Certificate = 1,

    /// <summary>The one protected build-provenance document.</summary>
    Provenance = 2,

    /// <summary>An exact x64 framework dependency MSIX.</summary>
    DependencyPackage = 3,
}

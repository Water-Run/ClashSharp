namespace ClashSharp.Installer.Contracts;

/// <summary>Defines fixed ProgramData path components shared by the Installer and App startup.</summary>
/// <remarks>
/// The App links this declarative contract without acquiring an Installer engine dependency.
/// Journal presence blocks startup until the elevated transaction authority clears verified state.
/// </remarks>
public static class InstallerStateLayout
{
    /// <summary>Gets the product directory below the Windows CommonApplicationData known folder.</summary>
    public const string ProductDirectoryName = "ClashSharp";

    /// <summary>Gets the protected Installer directory below the product directory.</summary>
    public const string InstallerDirectoryName = "Installer";

    /// <summary>Gets the current recovery-state directory below the Installer directory.</summary>
    public const string VersionDirectoryName = "v2";

    /// <summary>Gets the current transaction journal filename below the version directory.</summary>
    public const string JournalFileName = "transaction-v2.json";

    /// <summary>Gets the legacy transaction marker filename directly below the Installer directory.</summary>
    public const string LegacyMarkerFileName = "transaction.json";

    /// <summary>Gets the per-user App lifetime lock below LocalApplicationData and the product directory.</summary>
    public const string ApplicationMutationLockFileName = "InstallerMutation.lock";
}

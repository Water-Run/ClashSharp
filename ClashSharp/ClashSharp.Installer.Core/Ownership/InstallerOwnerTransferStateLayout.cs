namespace ClashSharp.Installer.Ownership;

/// <summary>Defines private owner-transfer state separately from the user-readable v2 journal.</summary>
public static class InstallerOwnerTransferStateLayout
{
    /// <summary>Gets the authority-only directory below the existing ProgramData product directory.</summary>
    public const string AuthorityDirectoryName = "InstallerAuthority";

    /// <summary>Gets the private schema directory below the authority directory.</summary>
    public const string VersionDirectoryName = "v1";

    /// <summary>Gets the only journal leaf accepted by owner-transfer persistence.</summary>
    public const string JournalFileName = "owner-transfer-v1.json";
}

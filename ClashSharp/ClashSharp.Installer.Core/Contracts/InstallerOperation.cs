namespace ClashSharp.Installer.Contracts;

/// <summary>Identifies the user-requested installation mutation.</summary>
public enum InstallerOperation
{
    /// <summary>Deploys the package and then provisions machine integration.</summary>
    Install,

    /// <summary>Revalidates and reapplies the package and machine integration.</summary>
    Repair,

    /// <summary>Removes machine integration and then removes the package.</summary>
    Uninstall,
}

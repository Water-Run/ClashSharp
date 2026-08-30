namespace ClashSharp.Installer.Contracts;

/// <summary>Describes the last durable commit in an installer transaction.</summary>
public enum InstallerTransactionPhase
{
    /// <summary>The immutable release identity and recovery intent are durable.</summary>
    Prepared,

    /// <summary>The elevated helper reserved compatible machine ownership.</summary>
    MachineReserved,

    /// <summary>The elevated helper durably authorized owner-checked machine removal.</summary>
    MachineRemovalAuthorized,

    /// <summary>The requested package deployment or removal has committed.</summary>
    PackageCommitted,

    /// <summary>The requested machine integration mutation has committed.</summary>
    MachineCommitted,

    /// <summary>The complete requested final state has been independently verified.</summary>
    Verified,
}

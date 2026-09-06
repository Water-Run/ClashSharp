namespace ClashSharp.Installer.Ownership;

/// <summary>Orders the durable boundaries of a separately authorized machine-owner transfer.</summary>
public enum InstallerOwnerTransferPhase
{
    /// <summary>Both owners and the exact continuation are durably recorded before mutation.</summary>
    Prepared,

    /// <summary>The ordinary continuation journal blocks app startup under the previous ACL.</summary>
    StartupBlocked,

    /// <summary>The service identified by the previous owner and credential is verified absent.</summary>
    PreviousServiceRemoved,

    /// <summary>All machine payload and service-data roots have the target owner's exact ACL.</summary>
    MachineAccessTransferred,

    /// <summary>The strict association contains the recorded new owner and credential.</summary>
    AssociationTransferred,

    /// <summary>Previous certificate ownership is retained and target ownership state is ready.</summary>
    CertificateStateTransferred,

    /// <summary>The ordinary installer state roots have the target owner's exact ACL.</summary>
    InstallerAccessTransferred,

    /// <summary>All transfer postconditions and the still-pending ordinary continuation are verified.</summary>
    Verified,
}

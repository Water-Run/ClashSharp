namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Describes which mutation requests the process currently admits.</summary>
public enum MutationAdmissionState
{
    /// <summary>Ordinary mutation requests may acquire admission leases.</summary>
    Open,

    /// <summary>Ordinary admission is closed while existing leases drain.</summary>
    Closing,

    /// <summary>Only an operation-ID-authorized recovery attempt may run.</summary>
    RecoveryOnly,

    /// <summary>A recovery attempt is draining for a pending shutdown.</summary>
    RecoveryClosing,

    /// <summary>All mutation admission is permanently closed for process shutdown.</summary>
    ClosedForShutdown,
}

/// <summary>Describes why ordinary mutation admission is being closed.</summary>
public enum MutationAdmissionClosure
{
    /// <summary>Admission reopens after the exclusive destructive lease is released.</summary>
    Destructive,

    /// <summary>Admission becomes terminal when the exclusive shutdown lease is granted.</summary>
    Shutdown,
}

namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Identifies whether the current process owns the primary application instance.</summary>
public enum PrimaryInstanceOwnership
{
    /// <summary>The current process owns primary-instance startup.</summary>
    Primary,

    /// <summary>Activation was redirected to an existing primary process.</summary>
    Redirected,
}

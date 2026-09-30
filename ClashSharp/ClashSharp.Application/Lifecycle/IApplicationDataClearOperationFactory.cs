namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Creates a clear-all operation bound to the current host and its data directory.</summary>
public interface IApplicationDataClearOperationFactory
{
    /// <summary>Validates ownership before requesting shutdown or deleting user data.</summary>
    IApplicationLifetimeMaintenance Create();
}

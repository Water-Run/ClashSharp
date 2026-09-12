namespace ClashSharp.ApplicationModel.Security;

/// <summary>Supplies the independently initialized App-owned controller credential to runtime consumers.</summary>
public interface IControllerCredentialProvider
{
    /// <summary>Reads the verified process credential without opening storage or acquiring mutation admission.</summary>
    /// <returns>The private credential; callers must not log, export, or include it in preference snapshots.</returns>
    string GetSecret();
}

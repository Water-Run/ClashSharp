namespace ClashSharp.Model;

/// <summary>Enumerates coverage levels for ClashSharp data packages.</summary>
public enum ClashDataPackageScope
{
    /// <summary>Include application settings only.</summary>
    Settings = 0,

    /// <summary>Include settings and proxy-configuration data.</summary>
    SettingsAndProxyConfiguration = 1,
}

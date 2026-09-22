using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Reads and changes actual UI configuration on the owning window thread, without preference storage.</summary>
public interface IAppearanceNativeSettings
{
    /// <summary>Reads the actual selected language, window theme and independently verified accent resources.</summary>
    AppearanceNativeConfiguration CaptureConfiguration();

    /// <summary>Installs the requested language in the resource resolver.</summary>
    /// <param name="language">Canonical selected language.</param>
    void ApplyLanguage(AppLanguage language);

    /// <summary>Installs the requested theme on the actual window root.</summary>
    /// <param name="theme">Canonical selected theme.</param>
    void ApplyTheme(AppThemeMode theme);

    /// <summary>Installs and verifies the complete application accent resource set.</summary>
    /// <param name="accent">The configured mode and retained custom color.</param>
    void ApplyAccent(AccentColorConfiguration accent);
}

/// <summary>Contains UI configuration observed in one synchronous operation on the window thread.</summary>
public sealed record AppearanceNativeConfiguration
{
    /// <summary>Creates an immutable and canonical UI configuration.</summary>
    /// <param name="language">Selected resource language.</param>
    /// <param name="theme">Requested window theme.</param>
    /// <param name="accent">Independently verified accent configuration.</param>
    public AppearanceNativeConfiguration(AppLanguage language, AppThemeMode theme, AccentColorConfiguration accent)
    {
        if (!Enum.IsDefined(language)) { throw new ArgumentOutOfRangeException(nameof(language)); }
        if (!Enum.IsDefined(theme)) { throw new ArgumentOutOfRangeException(nameof(theme)); }
        Language = language;
        Theme = theme;
        Accent = accent ?? throw new ArgumentNullException(nameof(accent));
    }

    /// <summary>Gets the selected resource language.</summary>
    public AppLanguage Language { get; }
    /// <summary>Gets the requested window theme.</summary>
    public AppThemeMode Theme { get; }
    /// <summary>Gets the configured accent whose resources were verified.</summary>
    public AccentColorConfiguration Accent { get; }
}

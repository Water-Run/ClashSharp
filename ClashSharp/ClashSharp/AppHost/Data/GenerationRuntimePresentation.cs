using System;
using System.Threading;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;

namespace ClashSharp.Hosting.Data;

/// <summary>Supplies generation presentation ownership without requiring a window during login recovery.</summary>
internal sealed record GenerationRuntimePresentation(
    Func<OwnedUiDispatcher> CreateDispatcher, IAppearanceNativeSettings Appearance, Func<bool> ExitRequested)
{
    /// <summary>Selects presentation boundaries lazily so the login helper never resolves the startup window.</summary>
    internal static GenerationRuntimePresentation Select(bool recoveryOnly,
        Func<GenerationRuntimePresentation> createWindow, Func<bool> exitRequested)
    {
        ArgumentNullException.ThrowIfNull(createWindow);
        ArgumentNullException.ThrowIfNull(exitRequested);
        return recoveryOnly
            ? new(() => new OwnedUiDispatcher(() => false, _ => false, CancellationToken.None),
                new RecoveryAppearance(), exitRequested)
            : createWindow();
    }

    // Existing data must be fully composed before journal recovery. These boundaries allow
    // repository ownership and disposal, but cannot claim or change an absent window's settings.
    private sealed class RecoveryAppearance : IAppearanceNativeSettings
    {
        public AppearanceNativeConfiguration CaptureConfiguration() => throw Unavailable();
        public void ApplyLanguage(AppLanguage language) => throw Unavailable();
        public void ApplyTheme(AppThemeMode theme) => throw Unavailable();
        public void ApplyAccent(AccentColorConfiguration accent) => throw Unavailable();
        private static InvalidOperationException Unavailable() => new("Appearance is unavailable during login proxy recovery.");
    }
}

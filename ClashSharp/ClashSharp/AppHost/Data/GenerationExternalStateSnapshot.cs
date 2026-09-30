using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Hosting.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Actual external state captured before a generation transition; no desired preferences or credentials are included.</summary>
internal sealed record GenerationExternalStateSnapshot(DataGenerationDescriptor Generation,
    AppearanceNativeConfiguration Appearance, bool StartupEnabled, NetworkSettingsConfiguration Network);

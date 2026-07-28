namespace ClashSharp.ViewModel;

/// <summary>Immutable proxy information snapshot used by <see cref="SettingsViewModel"/>.</summary>
/// <param name="ConfigPath">Managed core configuration path; never null.</param>
/// <param name="IsCoreBinaryAvailable">True when the bundled core binary exists.</param>
/// <param name="CoreBinaryPath">Expected core binary path; never null.</param>
/// <remarks>
/// Invariants: String values are never null.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
internal readonly record struct SettingsProxyInformation(
    string ConfigPath,
    bool IsCoreBinaryAvailable,
    string CoreBinaryPath);

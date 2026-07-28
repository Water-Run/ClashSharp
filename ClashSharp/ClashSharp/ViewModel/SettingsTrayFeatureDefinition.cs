namespace ClashSharp.ViewModel;

/// <summary>One taskbar tray menu feature exposed in settings.</summary>
internal readonly record struct SettingsTrayFeatureDefinition(
    string Id,
    string TitleKey,
    string DescriptionKey,
    string Glyph);

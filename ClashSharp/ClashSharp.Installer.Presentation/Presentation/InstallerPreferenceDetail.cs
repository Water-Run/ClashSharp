namespace ClashSharp.Installer.Presentation;

/// <summary>One labeled initial preference shown in an expandable installer preset summary.</summary>
/// <param name="Label">Localized preference name.</param>
/// <param name="Value">Localized value the preset writes for new settings.</param>
public sealed record InstallerPreferenceDetail(string Label, string Value);

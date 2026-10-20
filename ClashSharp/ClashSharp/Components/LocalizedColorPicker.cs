using System;
using ClashSharp.ApplicationModel.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SpectrumComponents = Microsoft.UI.Xaml.Controls.ColorSpectrumComponents;

namespace ClashSharp.Components;

/// <summary>Relocalizes the existing native picker template without replacing channel inputs or their drafts.</summary>
/// <remarks>Named parts follow the pinned WinUI 1.8 template. The native control retains color parsing, selection and keyboard behavior.</remarks>
internal sealed class LocalizedColorPicker : ColorPicker, ILocalizedPresentation
{
    private readonly Func<string, string> _getString;
    private ComboBox? _colorModel;

    public LocalizedColorPicker(Func<string, string> getString)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        RegisterPropertyChangedCallback(ColorSpectrumComponentsProperty, (_, _) => RefreshLocalization());
    }

    protected override void OnApplyTemplate()
    {
        if (_colorModel is not null) { _colorModel.SelectionChanged -= OnColorModelChanged; }
        base.OnApplyTemplate();
        _colorModel = GetTemplateChild("ColorRepresentationComboBox") as ComboBox;
        if (_colorModel is not null) { _colorModel.SelectionChanged += OnColorModelChanged; }
        RefreshLocalization();
    }

    private void OnColorModelChanged(object sender, SelectionChangedEventArgs args) => RefreshLocalization();

    public void RefreshLocalization()
    {
        foreach (string channel in new[] { "Red", "Green", "Blue", "Hue", "Saturation", "Value", "Alpha" })
        {
            string label = _getString("ColorPicker." + channel);
            if (GetTemplateChild(channel + "Label") is TextBlock text) { text.Text = label; }
            SetName(channel + "TextBox", label);
        }
        SetName("ColorRepresentationComboBox", _getString("ColorPicker.Model"));
        SetName("HexTextBox", _getString(_colorModel?.SelectedIndex == 1 ? "ColorPicker.HexHsv" : "ColorPicker.HexRgb"));
        SetName("ColorSpectrum", _getString("ColorPicker.Spectrum"));
        if (GetTemplateChild("ColorSpectrum") is DependencyObject spectrum)
        { AutomationProperties.SetHelpText(spectrum, _getString("ColorPicker.Spectrum.Help")); }
        string sliderChannel = ColorSpectrumComponents switch
        {
            SpectrumComponents.ValueSaturation or SpectrumComponents.SaturationValue => "Hue",
            SpectrumComponents.HueValue or SpectrumComponents.ValueHue => "Saturation",
            _ => "Value",
        };
        SetName("ThirdDimensionSlider", _getString("ColorPicker." + sliderChannel));
        SetName("AlphaSlider", _getString("ColorPicker.Alpha"));
    }

    private void SetName(string partName, string name)
    {
        if (GetTemplateChild(partName) is DependencyObject part) { AutomationProperties.SetName(part, name); }
    }
}

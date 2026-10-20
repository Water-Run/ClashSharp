using System;
using System.Collections.Generic;
using System.Threading;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpectrumComponents = Microsoft.UI.Xaml.Controls.ColorSpectrumComponents;

namespace ClashSharp.Components;

/// <summary>Updates an already-loaded native picker's labels without deriving from it or replacing its inputs.</summary>
/// <remarks>Named parts follow the pinned WinUI 1.8 template. The owner disposes the binding with the dialog.</remarks>
internal sealed class ColorPickerLocalization : IDisposable, ILocalizedPresentation
{
    private readonly Func<string, string> _getString;
    private readonly IApplicationErrorSink _errors;
    private ColorPicker? _picker;
    private ComboBox? _colorModel;
    private long? _spectrumToken;

    public ColorPickerLocalization(ColorPicker picker, Func<string, string> getString, IApplicationErrorSink errors)
    {
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        picker.Loaded += OnLoaded;
        picker.ActualThemeChanged += OnThemeChanged;
        _spectrumToken = picker.RegisterPropertyChangedCallback(ColorPicker.ColorSpectrumComponentsProperty,
            async (_, _) => await RefreshAtNativeBoundaryAsync());
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) => await RefreshAtNativeBoundaryAsync();
    private async void OnThemeChanged(FrameworkElement sender, object args) => await RefreshAtNativeBoundaryAsync();

    private async System.Threading.Tasks.Task RefreshAtNativeBoundaryAsync()
    {
        try { RefreshLocalization(); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            try { await _errors.ReportAsync(new ApplicationError("color-picker-localization", failure), CancellationToken.None); }
            catch (Exception sinkFailure) when (!ExceptionGraphClassifier.IsProcessFatal(sinkFailure))
            { /* The native event boundary has no independent diagnostic channel. */ }
        }
    }

    public void RefreshLocalization()
    {
        if (_picker is not { IsLoaded: true } picker) { return; }
        Dictionary<string, FrameworkElement> parts = ReadLoadedParts(picker);
        ComboBox? model = parts.GetValueOrDefault("ColorRepresentationComboBox") as ComboBox;
        if (!ReferenceEquals(model, _colorModel))
        {
            if (_colorModel is not null) { _colorModel.SelectionChanged -= OnColorModelChanged; }
            _colorModel = model;
            if (model is not null) { model.SelectionChanged += OnColorModelChanged; }
        }
        foreach (string channel in new[] { "Red", "Green", "Blue", "Hue", "Saturation", "Value", "Alpha" })
        {
            string label = _getString("ColorPicker." + channel);
            if (parts.GetValueOrDefault(channel + "Label") is TextBlock text) { text.Text = label; }
            SetName(parts, channel + "TextBox", label);
        }
        SetName(parts, "ColorRepresentationComboBox", _getString("ColorPicker.Model"));
        SetName(parts, "HexTextBox", _getString(model?.SelectedIndex == 1 ? "ColorPicker.HexHsv" : "ColorPicker.HexRgb"));
        SetName(parts, "ColorSpectrum", _getString("ColorPicker.Spectrum"));
        if (parts.TryGetValue("ColorSpectrum", out FrameworkElement? spectrum))
        { AutomationProperties.SetHelpText(spectrum, _getString("ColorPicker.Spectrum.Help")); }
        string channelKey = picker.ColorSpectrumComponents switch
        {
            SpectrumComponents.ValueSaturation or SpectrumComponents.SaturationValue => "Hue",
            SpectrumComponents.HueValue or SpectrumComponents.ValueHue => "Saturation",
            _ => "Value",
        };
        SetName(parts, "ThirdDimensionSlider", _getString("ColorPicker." + channelKey));
        SetName(parts, "AlphaSlider", _getString("ColorPicker.Alpha"));
    }

    private async void OnColorModelChanged(object sender, SelectionChangedEventArgs args) => await RefreshAtNativeBoundaryAsync();

    private static Dictionary<string, FrameworkElement> ReadLoadedParts(ColorPicker picker)
    {
        Dictionary<string, FrameworkElement> parts = new(StringComparer.Ordinal);
        Queue<DependencyObject> pending = new();
        pending.Enqueue(picker);
        while (pending.TryDequeue(out DependencyObject? current))
        {
            if (current is FrameworkElement element && !string.IsNullOrEmpty(element.Name)) { parts[element.Name] = element; }
            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < count; index++) { pending.Enqueue(VisualTreeHelper.GetChild(current, index)); }
        }
        return parts;
    }

    private static void SetName(Dictionary<string, FrameworkElement> parts, string name, string label)
    {
        if (parts.TryGetValue(name, out FrameworkElement? part)) { AutomationProperties.SetName(part, label); }
    }

    public void Dispose()
    {
        ColorPicker? picker = _picker;
        _picker = null;
        if (_colorModel is not null) { _colorModel.SelectionChanged -= OnColorModelChanged; _colorModel = null; }
        if (picker is null) { return; }
        picker.Loaded -= OnLoaded;
        picker.ActualThemeChanged -= OnThemeChanged;
        if (_spectrumToken is long token) { picker.UnregisterPropertyChangedCallback(ColorPicker.ColorSpectrumComponentsProperty, token); }
    }
}

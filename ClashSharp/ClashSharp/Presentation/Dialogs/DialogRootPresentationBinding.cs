using System;
using ClashSharp.ApplicationModel.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Keeps one admitted dialog aligned with its root without rebuilding its content or replaying operations.</summary>
/// <remarks>Created and disposed on the owning UI thread. Window teardown fences callbacks before the native root is destroyed.</remarks>
internal sealed class DialogRootPresentationBinding : IDisposable
{
    private readonly ContentDialog _dialog;
    private readonly DialogPresentationGate _gate;
    private FrameworkElement? _root;
    private long? _languageToken;
    private long? _directionToken;

    public DialogRootPresentationBinding(ContentDialog dialog, FrameworkElement? root, DialogPresentationGate gate)
    {
        _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _root = root;
        try
        {
            if (root is not null)
            {
                _languageToken = root.RegisterPropertyChangedCallback(FrameworkElement.LanguageProperty, OnLanguageChanged);
                _directionToken = root.RegisterPropertyChangedCallback(FrameworkElement.FlowDirectionProperty, OnDirectionChanged);
                root.ActualThemeChanged += OnThemeChanged;
            }
            Refresh(refreshText: true);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void OnLanguageChanged(DependencyObject sender, DependencyProperty property) => Refresh(refreshText: true);
    private void OnDirectionChanged(DependencyObject sender, DependencyProperty property) => Refresh(refreshText: false);
    private void OnThemeChanged(FrameworkElement sender, object args) => Refresh(refreshText: false);

    private void Refresh(bool refreshText)
    {
        FrameworkElement? root = _root;
        if (root is null) { return; }
        _gate.TryRefreshPresentation(() =>
        {
            _dialog.RequestedTheme = root.ActualTheme;
            _dialog.Language = root.Language;
            _dialog.FlowDirection = root.FlowDirection;
            if (refreshText && _dialog is ILocalizedPresentation localized) { localized.RefreshLocalization(); }
        });
    }

    public void Dispose()
    {
        FrameworkElement? root = _root;
        _root = null;
        if (root is null) { return; }
        root.ActualThemeChanged -= OnThemeChanged;
        if (_languageToken is long language) { root.UnregisterPropertyChangedCallback(FrameworkElement.LanguageProperty, language); }
        if (_directionToken is long direction) { root.UnregisterPropertyChangedCallback(FrameworkElement.FlowDirectionProperty, direction); }
    }
}

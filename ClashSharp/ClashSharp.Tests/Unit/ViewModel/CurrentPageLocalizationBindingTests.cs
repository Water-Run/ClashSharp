extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using Binding = ClashSharpUi::ClashSharp.Presentation.Composition.CurrentPageLocalizationBinding;
using Localization = ClashSharpUi::ClashSharp.Service.LocalizationService;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed class CurrentPageLocalizationBindingTests
{
    [Fact]
    public void AppliedLanguage_RefreshesTheCurrentPageWithoutRetainingThePreviousPage()
    {
        Localization localization = new();
        Target first = new();
        Target second = new();
        ILocalizedPresentation? current = first;
        using Binding binding = new(localization, () => current);

        localization.CurrentLanguage = AppLanguage.German;
        current = second;
        localization.CurrentLanguage = AppLanguage.Persian;
        current = null;
        localization.CurrentLanguage = AppLanguage.English;

        Assert.Equal(1, first.RefreshCount);
        Assert.Equal(1, second.RefreshCount);
    }

    [Fact]
    public void Disposal_StopsAccessToAClosedWindowAndIsIdempotent()
    {
        Localization localization = new();
        Target target = new();
        bool closed = false;
        using Binding binding = new(localization, () => closed
            ? throw new InvalidOperationException("window destroyed") : target);
        localization.CurrentLanguage = AppLanguage.German;

        binding.Dispose();
        binding.Dispose();
        closed = true;
        localization.CurrentLanguage = AppLanguage.Persian;

        Assert.Equal(1, target.RefreshCount);
    }

    private sealed class Target : ILocalizedPresentation
    {
        public int RefreshCount { get; private set; }
        public void RefreshLocalization() => RefreshCount++;
    }
}

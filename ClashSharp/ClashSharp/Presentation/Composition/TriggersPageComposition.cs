using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Hosting.Compatibility;

namespace ClashSharp.Presentation.Composition;

/// <summary>Composition boundary for the triggers page while WinUI owns page activation.</summary>
internal static class TriggersPageComposition
{
    public static TriggersPageDependencies Create()
    {
        IApplicationErrorSink errorSink = LegacyPageServiceBridge.CreateErrorSink();
        return new TriggersPageDependencies(
            TriggerPresentationCompatibilityFactory
                .RequireActive()
                .CreateViewModel(LegacyPageServiceBridge.Localization.GetString, errorSink),
            errorSink);
    }
}

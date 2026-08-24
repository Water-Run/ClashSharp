using System;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the triggers page.</summary>
internal static class TriggersPageComposition
{
    public static TriggersPageDependencies Create(
        PageCompositionContext context,
        Action openLogs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(openLogs);
        IApplicationErrorSink errorSink = context.ErrorSink;
        return new TriggersPageDependencies(
            context.TriggerPresentation.CreateViewModel(
                context.Localization.GetString,
                errorSink),
            errorSink,
            openLogs);
    }
}

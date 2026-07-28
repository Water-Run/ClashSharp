using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Immutable dependencies supplied to a WinUI-created triggers page.</summary>
internal sealed class TriggersPageDependencies
{
    public TriggersPageDependencies(
        TriggersViewModel viewModel,
        IApplicationErrorSink errorSink)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
    }

    public TriggersViewModel ViewModel { get; }

    public IApplicationErrorSink ErrorSink { get; }
}

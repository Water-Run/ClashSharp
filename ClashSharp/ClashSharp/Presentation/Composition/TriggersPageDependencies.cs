using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Immutable dependencies supplied to a WinUI-created triggers page.</summary>
internal sealed class TriggersPageDependencies
{
    public TriggersPageDependencies(
        TriggersViewModel viewModel,
        IApplicationErrorSink errorSink,
        Action openLogs,
        Func<Action, IDisposable> subscribeToDataChanges)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        OpenLogs = openLogs ?? throw new ArgumentNullException(nameof(openLogs));
        SubscribeToDataChanges = subscribeToDataChanges ?? throw new ArgumentNullException(nameof(subscribeToDataChanges));
    }

    public TriggersViewModel ViewModel { get; }

    public IApplicationErrorSink ErrorSink { get; }

    public Action OpenLogs { get; }
    public Func<Action, IDisposable> SubscribeToDataChanges { get; }
}

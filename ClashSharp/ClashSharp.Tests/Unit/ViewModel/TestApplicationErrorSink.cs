using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Records unexpected command errors for view-model unit tests.</summary>
internal sealed class TestApplicationErrorSink : IApplicationErrorSink
{
    /// <summary>Gets the errors reported during a test.</summary>
    public List<ApplicationError> Errors { get; } = [];

    /// <inheritdoc />
    public Task ReportAsync(
        ApplicationError applicationError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applicationError);
        cancellationToken.ThrowIfCancellationRequested();
        Errors.Add(applicationError);
        return Task.CompletedTask;
    }
}

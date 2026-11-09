using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// A command is released by the command itself, never by its observers. Interface bindings subscribe to
/// CanExecuteChanged and PropertyChanged, and a subscriber can throw (for example a COM failure while a window is
/// closing). Whatever a subscriber does, the command must neither stay busy nor refuse every later run.
/// </summary>
public sealed class AsyncRelayCommandSubscriberFaultTests
{
    public enum Subscription
    {
        CanExecuteChanged,
        IsRunningPropertyChanged,
        AnyPropertyChanged,
    }

    public enum EntryPoint
    {
        ICommandExecute,
        ExecuteAsync,
        ExecuteObservedAsync,
    }

    public static TheoryData<Subscription, EntryPoint> Combinations()
    {
        TheoryData<Subscription, EntryPoint> data = [];
        foreach (Subscription subscription in Enum.GetValues<Subscription>())
        {
            foreach (EntryPoint entry in Enum.GetValues<EntryPoint>())
            {
                data.Add(subscription, entry);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task AThrowingSubscriberNeverLeavesTheCommandBusyOrBlocksLaterRuns(Subscription subscription, EntryPoint entry)
    {
        int runs = 0;
        AsyncRelayCommand command = new(_ =>
        {
            runs++;
            return Task.CompletedTask;
        }, new SilentSink());
        Attach(command, subscription);

        // The first invocation may surface the subscriber's failure to its caller; it must still end cleanly.
        await Record.ExceptionAsync(() => Invoke(command, entry));

        Assert.False(command.IsRunning, "the command stayed busy after a subscriber failed");
        Assert.True(command.CanExecute(null), "the command refuses every later run");

        // And it keeps working, and keeps ending, for as long as the subscriber keeps failing.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            await Record.ExceptionAsync(() => Invoke(command, entry));
            Assert.False(command.IsRunning);
        }

        Assert.Equal(6, runs);
    }

    private static void Attach(AsyncRelayCommand command, Subscription subscription)
    {
        switch (subscription)
        {
            case Subscription.CanExecuteChanged:
                command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("scripted subscriber failure");
                break;
            case Subscription.IsRunningPropertyChanged:
                command.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(AsyncRelayCommand.IsRunning))
                    {
                        throw new InvalidOperationException("scripted subscriber failure");
                    }
                };
                break;
            default:
                command.PropertyChanged += (_, _) => throw new InvalidOperationException("scripted subscriber failure");
                break;
        }
    }

    private static Task Invoke(AsyncRelayCommand command, EntryPoint entry)
    {
        switch (entry)
        {
            case EntryPoint.ICommandExecute:
                command.Execute(null);
                return Task.CompletedTask;
            case EntryPoint.ExecuteAsync:
                return command.ExecuteAsync(null);
            default:
                return command.ExecuteObservedAsync(null, CancellationToken.None);
        }
    }

    private sealed class SilentSink : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

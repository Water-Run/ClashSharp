using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.Tests.Unit.Diagnostics;

/// <summary>Verifies process-wide exception graph classification.</summary>
public sealed class ExceptionGraphClassifierTests
{
    /// <summary>Verifies ordinary failures are recoverable without formatting hostile exception text.</summary>
    [Fact]
    public void Classify_OrdinaryFailure_ReturnsRecoverableWithoutReadingText()
    {
        HostileException exception = new();

        ExceptionGraphClassification classification =
            ExceptionGraphClassifier.Classify(exception);

        Assert.Equal(ExceptionGraphClassification.Recoverable, classification);
        Assert.Equal(0, exception.MessageReads);
        Assert.Equal(0, exception.ToStringCalls);
    }

    /// <summary>Verifies every supported process-fatal node wins at the graph root.</summary>
    [Theory]
    [MemberData(nameof(ProcessFatalFailures))]
    public void Classify_DirectProcessFatalFailure_ReturnsProcessFatal(Exception exception)
    {
        Assert.Equal(
            ExceptionGraphClassification.ProcessFatal,
            ExceptionGraphClassifier.Classify(exception));
    }

    /// <summary>Verifies wrapper and aggregate traversal cannot hide a process-fatal child.</summary>
    [Theory]
    [MemberData(nameof(ProcessFatalFailures))]
    public void Classify_WrappedProcessFatalFailure_ReturnsProcessFatal(Exception fatalFailure)
    {
        AggregateException exception = new(
            new IOException("ordinary sibling"),
            new InvalidOperationException("wrapper", fatalFailure));

        Assert.Equal(
            ExceptionGraphClassification.ProcessFatal,
            ExceptionGraphClassifier.Classify(exception));
    }

    /// <summary>Verifies cancellation wrappers are traversed for a process-fatal inner failure.</summary>
    [Fact]
    public void Classify_OperationCancellationWrappingFatalFailure_ReturnsProcessFatal()
    {
        OperationCanceledException exception = new(
            "cancelled",
            CreateProcessFatalException<OutOfMemoryException>(),
            CancellationToken.None);

        Assert.Equal(
            ExceptionGraphClassification.ProcessFatal,
            ExceptionGraphClassifier.Classify(exception));
    }

    /// <summary>Verifies a cancelled caller plus a cancellation-only graph is caller cancellation.</summary>
    [Fact]
    public void Classify_CancelledCallerAndCancellationOnlyGraph_ReturnsCallerCancellation()
    {
        using CancellationTokenSource callerCancellation = new();
        callerCancellation.Cancel();
        AggregateException exception = new(
            new OperationCanceledException(),
            new InvalidOperationException(
                "wrapper",
                new OperationCanceledException(callerCancellation.Token)));

        Assert.Equal(
            ExceptionGraphClassification.CallerCancellation,
            ExceptionGraphClassifier.Classify(exception, callerCancellation.Token));
    }

    /// <summary>Verifies cancellation remains unexpected while the caller token is active.</summary>
    [Fact]
    public void Classify_ActiveCallerAndCancellation_ReturnsUnexpectedCancellation()
    {
        using CancellationTokenSource callerCancellation = new();

        Assert.Equal(
            ExceptionGraphClassification.UnexpectedCancellation,
            ExceptionGraphClassifier.Classify(
                new OperationCanceledException(callerCancellation.Token),
                callerCancellation.Token));
    }

    /// <summary>Verifies an ordinary sibling prevents a mixed graph from being swallowed as cancellation.</summary>
    [Fact]
    public void Classify_CancelledCallerAndMixedGraph_ReturnsUnexpectedCancellation()
    {
        using CancellationTokenSource callerCancellation = new();
        callerCancellation.Cancel();
        AggregateException exception = new(
            new OperationCanceledException(callerCancellation.Token),
            new IOException("ordinary sibling"));

        Assert.Equal(
            ExceptionGraphClassification.UnexpectedCancellation,
            ExceptionGraphClassifier.Classify(exception, callerCancellation.Token));
    }

    /// <summary>Supplies the process-fatal node types recognized by the application contract.</summary>
    public static TheoryData<Exception> ProcessFatalFailures => new()
    {
        CreateProcessFatalException<OutOfMemoryException>(),
        CreateProcessFatalException<StackOverflowException>(),
        CreateProcessFatalException<AccessViolationException>(),
    };

    private static TException CreateProcessFatalException<TException>()
        where TException : Exception =>
        Activator.CreateInstance<TException>();

    private sealed class HostileException : Exception
    {
        public int MessageReads { get; private set; }

        public int ToStringCalls { get; private set; }

        public override string Message
        {
            get
            {
                MessageReads++;
                throw new InvalidOperationException("Message is unavailable.");
            }
        }

        public override string ToString()
        {
            ToStringCalls++;
            throw new InvalidOperationException("Formatting is unavailable.");
        }
    }
}

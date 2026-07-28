using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Tests.Unit.Startup;

public sealed class StartupCompletionFailurePolicyTests
{
    [Fact]
    public void IsRecoverable_OrdinaryFailure_ReturnsTrueWithoutReadingExceptionText()
    {
        HostileException exception = new();

        Assert.True(StartupCompletionFailurePolicy.IsRecoverable(exception));
        Assert.Equal(0, exception.MessageReads);
        Assert.Equal(0, exception.ToStringCalls);
    }

    [Theory]
    [MemberData(nameof(ProcessFatalExceptions))]
    public void IsRecoverable_CancellationOrProcessFatalFailure_ReturnsFalse(Exception exception)
    {
        Assert.False(StartupCompletionFailurePolicy.IsRecoverable(exception));
    }

    [Theory]
    [MemberData(nameof(ProcessFatalExceptions))]
    public void IsRecoverable_InnerProcessFatalFailure_ReturnsFalse(Exception exception)
    {
        InvalidOperationException wrapper = new("shutdown wrapper", exception);

        Assert.False(StartupCompletionFailurePolicy.IsRecoverable(wrapper));
    }

    [Theory]
    [MemberData(nameof(ProcessFatalExceptions))]
    public void IsRecoverable_AggregateContainingProcessFatalFailure_ReturnsFalse(Exception exception)
    {
        AggregateException wrapper = new(
            new IOException("ordinary sibling"),
            new InvalidOperationException("nested wrapper", exception));

        Assert.False(StartupCompletionFailurePolicy.IsRecoverable(wrapper));
    }

    [Fact]
    public void IsRecoverable_OrdinaryAggregate_ReturnsTrueWithoutReadingExceptionText()
    {
        HostileException hostile = new();
        AggregateException wrapper = new(
            new InvalidOperationException("ordinary"),
            hostile);

        Assert.True(StartupCompletionFailurePolicy.IsRecoverable(wrapper));
        Assert.Equal(0, hostile.MessageReads);
        Assert.Equal(0, hostile.ToStringCalls);
    }

    public static TheoryData<Exception> ProcessFatalExceptions => new()
    {
        new OperationCanceledException(),
        CreateException<OutOfMemoryException>(),
        CreateException<StackOverflowException>(),
        CreateException<AccessViolationException>(),
    };

    private static TException CreateException<TException>()
        where TException : Exception, new() =>
        new();

    private sealed class HostileException : Exception
    {
        public int MessageReads { get; private set; }

        public int ToStringCalls { get; private set; }

        public override string Message
        {
            get
            {
                MessageReads++;
                throw new InvalidOperationException("message unavailable");
            }
        }

        public override string ToString()
        {
            ToStringCalls++;
            throw new InvalidOperationException("formatting unavailable");
        }
    }
}

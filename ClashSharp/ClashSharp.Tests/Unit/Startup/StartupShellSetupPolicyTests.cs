extern alias ClashSharpUi;

using StartupShellSetupPolicy =
    ClashSharpUi::ClashSharp.Hosting.Startup.StartupShellSetupPolicy;

namespace ClashSharp.Tests.Unit.Startup;

/// <summary>Verifies recoverable startup-shell decoration failures cannot remove the primary window.</summary>
public sealed class StartupShellSetupPolicyTests
{
    [Fact]
    public void TryRun_OrdinarySetupFailure_ContainsFailure()
    {
        Exception? escaped = Record.Exception(
            () => StartupShellSetupPolicy.TryRun(
                () => throw new InvalidOperationException("optional setup unavailable")));

        Assert.Null(escaped);
    }

    [Fact]
    public void TryRun_OperationCancellation_DoesNotContainFailure()
    {
        OperationCanceledException expected = new();

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(
            () => StartupShellSetupPolicy.TryRun(() => throw expected));

        Assert.Same(expected, actual);
    }

    [Theory]
    [MemberData(nameof(ProcessFatalExceptions))]
    public void TryRun_ProcessFatalFailure_DoesNotContainFailure(Exception expected)
    {
        Exception actual = Assert.Throws(
            expected.GetType(),
            () => StartupShellSetupPolicy.TryRun(() => throw expected));

        Assert.Same(expected, actual);
    }

    [Theory]
    [MemberData(nameof(ProcessFatalExceptions))]
    public void TryRun_WrappedProcessFatalFailure_DoesNotContainFailure(Exception fatal)
    {
        InvalidOperationException expected = new("shell wrapper", fatal);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => StartupShellSetupPolicy.TryRun(() => throw expected));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void TryRun_HostileRecoverableFailure_DoesNotInspectExceptionText()
    {
        HostileException exception = new();

        Exception? escaped = Record.Exception(
            () => StartupShellSetupPolicy.TryRun(() => throw exception));

        Assert.Null(escaped);
        Assert.Equal(0, exception.MessageReads);
        Assert.Equal(0, exception.ToStringCalls);
    }

    public static TheoryData<Exception> ProcessFatalExceptions => new()
    {
        CreateException<OutOfMemoryException>(),
        CreateException<StackOverflowException>(),
        CreateException<AccessViolationException>(),
    };

    private static TException CreateException<TException>()
        where TException : Exception =>
        Assert.IsType<TException>(Activator.CreateInstance<TException>());

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

using System.Text.RegularExpressions;
using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.Tests.Unit.Diagnostics;

/// <summary>Verifies concurrent bounded diagnostic text capture.</summary>
public sealed partial class ConcurrentBoundedTextBufferTests
{
    /// <summary>Verifies concurrent writers and readers observe only complete lines within the limit.</summary>
    [Fact]
    public async Task ConcurrentWritesAndSnapshots_RemainBoundedAndLineComplete()
    {
        const int capacity = 4096;
        ConcurrentBoundedTextBuffer buffer = new(capacity);
        TaskCompletionSource<object?> writersCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reader = Task.Run(async () =>
        {
            while (!writersCompleted.Task.IsCompleted)
            {
                AssertValidSnapshot(buffer.Snapshot(), capacity);
                await Task.Yield();
            }

            AssertValidSnapshot(buffer.Snapshot(), capacity);
        });
        Task[] writers = Enumerable.Range(0, 8)
            .Select(writer => Task.Run(() =>
            {
                for (int index = 0; index < 2000; index++)
                {
                    buffer.TryAppendLine($"stream-{writer}:{index:D5}");
                }
            }))
            .ToArray();

        await Task.WhenAll(writers);
        writersCompleted.TrySetResult(null);
        await reader;
        buffer.Complete();

        string snapshot = buffer.Snapshot();
        AssertValidSnapshot(snapshot, capacity);
        Assert.Contains(ConcurrentBoundedTextBuffer.TruncationMarker, snapshot, StringComparison.Ordinal);
    }

    /// <summary>Verifies overflow preserves a deterministic complete-line prefix and marker.</summary>
    [Fact]
    public void Overflow_AppendsDeterministicTruncationMarkerWithinCapacity()
    {
        string markerLine = ConcurrentBoundedTextBuffer.TruncationMarker + Environment.NewLine;
        int capacity = "alpha".Length + Environment.NewLine.Length
            + "beta".Length + Environment.NewLine.Length
            + markerLine.Length;
        ConcurrentBoundedTextBuffer buffer = new(capacity);

        Assert.True(buffer.TryAppendLine("alpha"));
        Assert.True(buffer.TryAppendLine("beta"));
        Assert.False(buffer.TryAppendLine("this-line-does-not-fit-at-all"));
        Assert.False(buffer.TryAppendLine("ignored-after-truncation"));

        Assert.Equal(
            "alpha" + Environment.NewLine
            + "beta" + Environment.NewLine
            + markerLine,
            buffer.Snapshot());
        Assert.True(buffer.IsTruncated);
    }

    /// <summary>Verifies completion permanently rejects later writes and freezes the snapshot.</summary>
    [Fact]
    public void Complete_RejectsWritesAndKeepsStableSnapshot()
    {
        ConcurrentBoundedTextBuffer buffer = new(128);
        Assert.True(buffer.TryAppendLine("before"));

        buffer.Complete();
        string completed = buffer.Snapshot();

        Assert.True(buffer.IsCompleted);
        Assert.False(buffer.TryAppendLine("after"));
        Assert.False(buffer.TryAppendLine(null));
        Assert.Equal(completed, buffer.Snapshot());
    }

    private static void AssertValidSnapshot(string snapshot, int capacity)
    {
        Assert.InRange(snapshot.Length, 0, capacity);
        if (snapshot.Length == 0)
        {
            return;
        }

        Assert.EndsWith(Environment.NewLine, snapshot, StringComparison.Ordinal);
        string[] lines = snapshot.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.True(
            DiagnosticLinePattern().IsMatch(line)
            || string.Equals(line, ConcurrentBoundedTextBuffer.TruncationMarker, StringComparison.Ordinal),
            $"Unexpected or partial diagnostic line: {line}"));
    }

    [GeneratedRegex("^stream-[0-7]:[0-9]{5}$", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticLinePattern();
}

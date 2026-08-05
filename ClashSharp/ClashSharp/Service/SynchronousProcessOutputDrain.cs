using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.Service;

/// <summary>Owns synchronous pipe-reader threads for one App-owned mihomo generation.</summary>
internal sealed class SynchronousProcessOutputDrain
{
    private readonly object _failureLock = new();
    private readonly List<Exception> _failures = [];
    private readonly Thread[] _threads;

    public SynchronousProcessOutputDrain(
        StreamReader? standardOutput,
        StreamReader? standardError,
        ConcurrentBoundedTextBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        List<Thread> threads = [];
        AddReaderThread(threads, standardOutput, destination, "ClashSharp.Mihomo.Stdout");
        AddReaderThread(threads, standardError, destination, "ClashSharp.Mihomo.Stderr");
        _threads = [.. threads];

        foreach (Thread thread in _threads)
        {
            thread.Start();
        }
    }

    /// <summary>Waits until every owned reader reaches EOF.</summary>
    public void WaitForCompletion(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (Thread thread in _threads)
        {
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            if (!thread.Join(remaining))
            {
                throw new TimeoutException(
                    "The mihomo output readers did not reach EOF before the ownership handoff timeout.");
            }
        }
    }

    /// <summary>Surfaces unexpected pipe-read failures after both readers have completed.</summary>
    public void ThrowIfFailed()
    {
        Exception[] failures;
        lock (_failureLock)
        {
            failures = [.. _failures];
        }

        if (failures.Length > 0)
        {
            throw new AggregateException("The mihomo output readers failed.", failures);
        }
    }

    private void AddReaderThread(
        List<Thread> threads,
        StreamReader? reader,
        ConcurrentBoundedTextBuffer destination,
        string name)
    {
        if (reader is null)
        {
            return;
        }

        threads.Add(new Thread(() => Drain(reader, destination))
        {
            IsBackground = true,
            Name = name,
        });
    }

    private void Drain(StreamReader reader, ConcurrentBoundedTextBuffer destination)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                destination.TryAppendLine(line);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            lock (_failureLock)
            {
                _failures.Add(exception);
            }
        }
    }
}

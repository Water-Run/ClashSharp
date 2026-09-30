using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Lifecycle;

namespace ClashSharp.Service;

/// <summary>Separates admitted preference deletion from file deletion after all owning resources close.</summary>
internal sealed class ApplicationDataClearOperation : IApplicationLifetimeMaintenance
{
    private readonly Func<CancellationToken, Task<RuntimeShutdownResult>> _prepareShutdown;
    private readonly Action _clearHostData;
    private readonly string _dataDirectory;
    private readonly Func<bool> _repositoriesReleased;
    private RuntimeShutdownResult? _shutdownResult;
    private bool _hostDataCleared;

    internal ApplicationDataClearOperation(
        Func<CancellationToken, Task<RuntimeShutdownResult>> prepareShutdown,
        Action clearHostData,
        string dataDirectory,
        Func<bool>? repositoriesReleased = null)
    {
        _prepareShutdown = prepareShutdown ?? throw new ArgumentNullException(nameof(prepareShutdown));
        _clearHostData = clearHostData ?? throw new ArgumentNullException(nameof(clearHostData));
        _repositoriesReleased = repositoriesReleased ?? (() => true);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (string.Equals(_dataDirectory, Path.GetPathRoot(_dataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An application data directory cannot be a volume root.", nameof(dataDirectory));
        }
    }

    public async Task PrepareShutdownAsync(CancellationToken cancellationToken)
    {
        _shutdownResult = await _prepareShutdown(cancellationToken).ConfigureAwait(false);
        if (_shutdownResult.Outcome != RuntimeShutdownOutcome.PreparedForHostDisposal)
        {
            throw new RuntimeShutdownNotPreparedException(_shutdownResult);
        }
    }

    public Task ClearHostDataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_shutdownResult is not { Outcome: RuntimeShutdownOutcome.PreparedForHostDisposal, ErrorCode: null }
            || _shutdownResult.DegradedParticipants.Count != 0)
        {
            throw new InvalidOperationException("Data cannot be removed after an unverified or degraded shutdown.");
        }

        _clearHostData();
        _hostDataCleared = true;
        return Task.CompletedTask;
    }

    public Task ClearLocalFilesAsync(CancellationToken cancellationToken)
    {
        if (!_hostDataCleared)
        {
            return Task.FromException(new InvalidOperationException("Host-owned data must be cleared before local files."));
        }
        if (!_repositoriesReleased())
        {
            return Task.FromException(new InvalidOperationException("Data repositories must be released before file deletion."));
        }

        return Task.Run(ClearLocalFiles, cancellationToken);
    }

    private void ClearLocalFiles()
    {
        try
        {
            RejectReparsePath(_dataDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (FileNotFoundException)
        {
            return;
        }

        List<Exception> failures = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(_dataDirectory))
        {
            DeleteEntry(entry, failures);
        }

        if (failures.Count != 0)
        {
            throw new AggregateException("Some local application data could not be removed.", failures);
        }

        using IEnumerator<string> remaining = Directory.EnumerateFileSystemEntries(_dataDirectory).GetEnumerator();
        if (remaining.MoveNext())
        {
            throw new IOException("Local application data remained after cleanup.");
        }
    }

    private void DeleteEntry(string path, List<Exception> failures)
    {
        try
        {
            string resolvedPath = Path.GetFullPath(path);
            if (!resolvedPath.StartsWith(_dataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("A data entry escaped the application data directory.");
            }

            FileAttributes attributes = RejectReparsePath(resolvedPath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(resolvedPath))
                {
                    DeleteEntry(entry, failures);
                }

                Directory.Delete(resolvedPath, recursive: false);
            }
            else
            {
                File.Delete(resolvedPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(exception);
        }
    }

    private static FileAttributes RejectReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Data cleanup cannot traverse a reparse point.");
        }

        return attributes;
    }

    private static FileAttributes RejectReparsePath(string path)
    {
        FileAttributes attributes = RejectReparsePoint(path);
        for (DirectoryInfo? parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
        {
            RejectReparsePoint(parent.FullName);
        }
        return attributes;
    }
}

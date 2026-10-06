using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Infrastructure.Data;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Hosting.Data;

/// <summary>Removes only the disposed candidate of a completed, unpromoted abort.</summary>
internal sealed class GenerationCandidateDirectoryCleanup(string applicationDataRoot, MutationAdmissionBarrier admission,
    Action<string>? checkpoint = null)
{
    private const int MaximumEntries = 100_000;
    private const int MaximumDepth = 64;
    private readonly DataGenerationPathPolicy _paths = new(applicationDataRoot);

    public Task DeleteAbortedAsync(DataGenerationTransition transition, IGenerationReplacementJournal journal,
        MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(journal);
        admission.EnsureActiveExclusiveLease(lease);
        cancellationToken.ThrowIfCancellationRequested();
        if (!transition.IsAborted || transition.IsManifestPromoted || transition.IsCommitted || transition.IsSwapped
            || transition.StagedScopeState != DataGenerationScopeState.Disposed || transition.StagedDescriptor is not { } candidate)
        {
            throw new InvalidOperationException("Candidate deletion requires a fully retired, unpromoted abort.");
        }
        if (candidate.GenerationId == transition.BaselineManifest.Descriptor.GenerationId
            || !DataGenerationPathPolicy.IsContainedBy(_paths.GenerationsRootPath, candidate.RootPath)
            || !string.Equals(candidate.RootPath, _paths.GetGenerationRootPath(candidate.GenerationId), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The candidate cleanup root is not independently owned.");
        }

        Dictionary<string, SafeFileHandle> pinned = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            PinHierarchy(_paths.GenerationsRootPath, pinned);
            using SafeFileHandle pointer = ReparseSafeFile.OpenRead(_paths.CurrentManifestPath, FileShare.Read);
            using SafeFileHandle recovery = ReparseSafeFile.OpenRead(_paths.ValidateStagingPath(
                Path.Combine(_paths.DataRootPath, FileGenerationReplacementJournal.FileName)), FileShare.Read);
            DataGenerationManifestSnapshot? current = await new FileDataGenerationStore(applicationDataRoot)
                .LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (current?.ContentHash != transition.BaselineManifest.ContentHash
                || !current.Descriptor.IsSameGeneration(transition.BaselineManifest.Descriptor)
                || await journal.ReadPendingAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("Candidate cleanup requires the verified baseline and no pending recovery.");
            }
            PinHierarchy(current.Descriptor.RootPath, pinned);
            if (!TryReadAttributes(candidate.RootPath, out FileAttributes rootAttributes)) { return; }
            if ((rootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
            {
                throw new IOException("The candidate cleanup root is not an ordinary directory.");
            }
            PinHierarchy(candidate.RootPath, pinned);
            _paths.ValidateDescriptor(candidate);
            List<string> files = [];
            List<string> directories = [];
            Stack<(string Path, int Depth)> pending = new();
            pending.Push((candidate.RootPath, 0));
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (directory.Depth > MaximumDepth) { throw new IOException("The candidate cleanup tree exceeds its depth limit."); }
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    if (files.Count + directories.Count >= MaximumEntries) { throw new IOException("The candidate cleanup tree exceeds its entry limit."); }
                    string path = ValidateEntry(candidate.RootPath, entry);
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new IOException("Candidate cleanup cannot traverse a reparse point."); }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        PinHierarchy(path, pinned);
                        directories.Add(path);
                        pending.Push((path, directory.Depth + 1));
                    }
                    else { files.Add(path); }
                }
            }
            checkpoint?.Invoke("tree-pinned");
            cancellationToken.ThrowIfCancellationRequested();
            admission.EnsureActiveExclusiveLease(lease);
            _paths.ValidateDescriptor(candidate);
            string marker = Path.Combine(candidate.RootPath, DataGenerationIdentityMarker.FileName);
            foreach (string file in files.Where(file => !string.Equals(file, marker, StringComparison.OrdinalIgnoreCase)))
            {
                ValidateEntry(candidate.RootPath, file);
                File.Delete(file);
            }
            for (int index = directories.Count - 1; index >= 0; --index)
            {
                string directory = ValidateEntry(candidate.RootPath, directories[index]);
                pinned[directory].Dispose();
                Directory.Delete(directory, recursive: false);
            }
            string[] remaining = Directory.GetFileSystemEntries(candidate.RootPath);
            if (remaining.Length != 1 || !string.Equals(remaining[0], marker, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The candidate changed before final cleanup.");
            }
            ValidateEntry(candidate.RootPath, marker);
            File.Delete(marker);
            pinned[candidate.RootPath].Dispose();
            Directory.Delete(candidate.RootPath, recursive: false);
        }
        finally
        {
            foreach (SafeFileHandle handle in pinned.Values) { handle.Dispose(); }
        }
    }, cancellationToken);
    }

    private string ValidateEntry(string candidateRoot, string path)
    {
        string normalized = _paths.ValidateStagingPath(path);
        if (!DataGenerationPathPolicy.IsContainedBy(candidateRoot, normalized)) { throw new IOException("A cleanup entry escaped its candidate."); }
        if ((File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0) { throw new IOException("A cleanup entry became a reparse point."); }
        return normalized;
    }

    private static void PinHierarchy(string path, IDictionary<string, SafeFileHandle> pinned)
    {
        Stack<string> ancestors = new();
        for (string? current = Path.GetFullPath(path); current is not null && !pinned.ContainsKey(current); current = Path.GetDirectoryName(current))
        {
            ancestors.Push(current);
        }
        while (ancestors.TryPop(out string? ancestor)) { pinned.Add(ancestor, ReparseSafeFile.OpenDirectoryReadLock(ancestor)); }
    }

    private static bool TryReadAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { attributes = default; return false; }
        catch (DirectoryNotFoundException) { attributes = default; return false; }
    }
}

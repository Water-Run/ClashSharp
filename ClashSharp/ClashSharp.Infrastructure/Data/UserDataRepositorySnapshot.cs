using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Infrastructure.Data;

/// <summary>Copies allowlisted user repositories and committed SQLite pages into an unpublished data generation.</summary>
/// <remarks>The caller must pause producers and drain repository operations before taking a replacement snapshot.</remarks>
public sealed class UserDataRepositorySnapshot
{
    private static readonly string[] DocumentFiles =
    [
        "ProfileCatalog.json", "Triggers.json", "Triggers.json.migration-intent",
        Path.Combine("mihomo", "proxy-selections.json")
    ];
    private static readonly string[] DatabaseFiles = ["ClashSharpLogs.sqlite3", "Triggers.db", "Triggers.db.backup"];
    private static readonly string[] ProfileDirectories = ["profiles", "history"];
    private readonly string _sourceRoot;
    private readonly DataGenerationPathPolicy _paths;
    private readonly MutationAdmissionBarrier _admission;

    /// <summary>Creates a snapshot boundary without allocating or opening repositories.</summary>
    /// <param name="applicationDataRoot">Canonical application data root owning the candidate.</param>
    /// <param name="sourceRoot">Legacy root or a verified current generation root.</param>
    /// <param name="admission">Process-wide exclusive mutation owner.</param>
    public UserDataRepositorySnapshot(string applicationDataRoot, string sourceRoot, MutationAdmissionBarrier admission)
    {
        _paths = new(applicationDataRoot);
        _sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDataRoot)), _sourceRoot, StringComparison.OrdinalIgnoreCase)
            && !DataGenerationPathPolicy.IsContainedBy(_paths.GenerationsRootPath, _sourceRoot))
        {
            throw new InvalidDataException("A repository snapshot source must belong to this application.");
        }
    }

    /// <summary>Copies user data without carrying credentials, generated core state, or settings application evidence.</summary>
    /// <param name="candidate">Fresh, owned destination whose files must not already exist.</param>
    /// <param name="admissionLease">Exclusive ownership retained for the entire snapshot.</param>
    /// <param name="requireExisting">Requires the primary repositories when copying an established generation.</param>
    /// <param name="cancellationToken">Cancels preparation without publishing or deleting either generation.</param>
    public async Task CopyAdmittedAsync(DataGenerationDescriptor candidate, MutationAdmissionLease admissionLease,
        bool requireExisting, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(admissionLease);
        cancellationToken.ThrowIfCancellationRequested();
        _paths.ValidateDescriptor(candidate);
        if (string.Equals(_sourceRoot, candidate.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A repository snapshot needs a distinct destination.");
        }
        if (requireExisting)
        {
            foreach (string required in new[] { "ProfileCatalog.json", "ClashSharpLogs.sqlite3", "Triggers.db" })
            {
                if (!TryGetAttributes(GetSourcePath(required), out FileAttributes attributes)
                    || (attributes & FileAttributes.Directory) != 0)
                {
                    throw new InvalidDataException("An established generation is missing a required repository.");
                }
            }
        }
        foreach (string relativePath in DocumentFiles)
        {
            await CopyDocumentIfPresentAsync(candidate, relativePath, cancellationToken).ConfigureAwait(false);
        }
        foreach (string directory in ProfileDirectories)
        {
            await CopyDirectoryIfPresentAsync(candidate, Path.Combine("mihomo", directory), cancellationToken).ConfigureAwait(false);
        }
        foreach (string relativePath in DatabaseFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotDatabaseIfPresent(candidate, relativePath, cancellationToken);
        }
        _admission.EnsureActiveExclusiveLease(admissionLease);
    }

    private async Task CopyDirectoryIfPresentAsync(
        DataGenerationDescriptor candidate, string relativePath, CancellationToken cancellationToken)
    {
        string source = GetSourcePath(relativePath);
        if (!TryGetAttributes(source, out FileAttributes attributes)) { return; }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException("A source profile directory is occupied by a file.");
        }
        ValidateSource(source);
        Stack<string> pending = new();
        pending.Push(relativePath);
        while (pending.TryPop(out string? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = GetSourcePath(current);
            ValidateSource(directory);
            Directory.CreateDirectory(GetTargetPath(candidate, current));
            foreach (string child in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSource(child);
                string childRelative = Path.GetRelativePath(_sourceRoot, child);
                if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) { pending.Push(childRelative); }
                else { await CopyDocumentIfPresentAsync(candidate, childRelative, cancellationToken).ConfigureAwait(false); }
            }
        }
    }

    private async Task CopyDocumentIfPresentAsync(
        DataGenerationDescriptor candidate, string relativePath, CancellationToken cancellationToken)
    {
        string source = GetSourcePath(relativePath);
        if (!TryGetAttributes(source, out FileAttributes attributes)) { return; }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("A source data file is occupied by a directory.");
        }
        ValidateSource(source);
        string target = GetTargetPath(candidate, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        _paths.ValidateStagingPath(target);
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private void SnapshotDatabaseIfPresent(DataGenerationDescriptor candidate, string relativePath, CancellationToken cancellationToken)
    {
        string source = GetSourcePath(relativePath);
        if (!TryGetAttributes(source, out FileAttributes attributes)) { return; }
        if ((attributes & FileAttributes.Directory) != 0) { throw new InvalidDataException("A source database is occupied by a directory."); }
        ValidateSource(source);
        // SQLite may read these alongside the main database. Reject redirected companions too.
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (TryGetAttributes(source + suffix, out _)) { ValidateSource(source + suffix); }
        }
        string target = GetTargetPath(candidate, relativePath);
        if (File.Exists(target)) { throw new IOException("A repository snapshot cannot replace an existing candidate database."); }
        using SqliteConnection input = new(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        using (SqliteConnection output = new(new SqliteConnectionStringBuilder
        {
            DataSource = target,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            input.Open();
            cancellationToken.ThrowIfCancellationRequested();
            output.Open();
            // The backup API includes committed WAL pages; copying only the main file loses them.
            input.BackupDatabase(output);
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand check = output.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            using SqliteDataReader rows = check.ExecuteReader();
            if (!rows.Read() || !string.Equals(rows.GetString(0), "ok", StringComparison.Ordinal) || rows.Read())
            {
                throw new InvalidDataException("The snapshot database did not pass integrity verification.");
            }
        }
        using FileStream durable = new(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        durable.Flush(flushToDisk: true);
    }

    private string GetSourcePath(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(_sourceRoot, relativePath));
        if (!DataGenerationPathPolicy.IsContainedBy(_sourceRoot, path)) { throw new InvalidDataException("Source data path escaped its root."); }
        return path;
    }

    private string GetTargetPath(DataGenerationDescriptor candidate, string relativePath)
    {
        _paths.ValidateDescriptor(candidate);
        string path = Path.GetFullPath(Path.Combine(candidate.RootPath, relativePath));
        if (!DataGenerationPathPolicy.IsContainedBy(candidate.RootPath, path)) { throw new InvalidDataException("Candidate path escaped its generation."); }
        return path;
    }

    private static void ValidateSource(string path) => DataGenerationPathPolicy.ValidateNoReparsePoints(path, File.GetAttributes);

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { attributes = default; return false; }
        catch (DirectoryNotFoundException) { attributes = default; return false; }
    }
}

using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Infrastructure.Data;

/// <summary>Copies legacy user repositories and canonical preferences into an unpublished first generation.</summary>
/// <remarks>Call only after retained legacy transactions have recovered and before runtime producers start.</remarks>
public sealed class LegacyDataGenerationPreparer
{
    private static readonly string[] DocumentFiles =
    [
        "ProfileCatalog.json", "Triggers.json", "Triggers.json.migration-intent",
        Path.Combine("mihomo", "proxy-selections.json")
    ];
    private static readonly string[] DatabaseFiles = ["ClashSharpLogs.sqlite3", "Triggers.db", "Triggers.db.backup"];
    private static readonly string[] ProfileDirectories = ["profiles", "history"];
    private readonly string _legacyRoot;
    private readonly DataGenerationPathPolicy _paths;
    private readonly MutationAdmissionBarrier _admission;
    private readonly ILegacySettingsSource _legacySettings;
    private readonly SettingsRegistry _registry;

    /// <summary>Creates a preparer without inspecting legacy storage or allocating a generation.</summary>
    /// <param name="applicationDataRoot">Canonical application root containing the legacy repositories.</param>
    /// <param name="admission">Process-wide ownership barrier shared with bootstrap.</param>
    /// <param name="legacySettings">Read-only legacy preference source; credentials are excluded.</param>
    /// <param name="registry">Canonical settings definitions.</param>
    public LegacyDataGenerationPreparer(
        string applicationDataRoot, MutationAdmissionBarrier admission,
        ILegacySettingsSource legacySettings, SettingsRegistry registry)
    {
        _paths = new(applicationDataRoot);
        _legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDataRoot));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _legacySettings = legacySettings ?? throw new ArgumentNullException(nameof(legacySettings));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Creates a complete data copy without replacing legacy files or publishing a current manifest.</summary>
    /// <remarks>Failed candidates are retained for diagnosis; a retry always allocates a different immutable root.</remarks>
    /// <param name="admissionLease">Exclusive startup ownership covering retained transaction recovery and migration.</param>
    /// <param name="cancellationToken">Cancels copying or initialization before publication.</param>
    /// <returns>The candidate to verify with the actual repository container before manifest promotion.</returns>
    public async Task<DataGenerationDescriptor> PrepareAdmittedAsync(
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(admissionLease);
        cancellationToken.ThrowIfCancellationRequested();
        if (await new FileDataGenerationStore(_legacyRoot).LoadCurrentAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException("Legacy migration cannot replace an existing data generation.");
        }

        DataGenerationDescriptor candidate = _paths.CreateGeneration(Guid.NewGuid(), 1);
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
        JsonSettingsRepository repository = new(candidate, _registry);
        SettingsAuthorityBootstrapper bootstrapper = new(repository, _legacySettings, new(_registry));
        SettingsPersistenceResult preferences = await bootstrapper.OpenAsync(candidate.GenerationId, cancellationToken).ConfigureAwait(false);
        if (!preferences.IsSucceeded || preferences.Envelope is null)
        {
            throw new InvalidDataException($"Initial settings could not be verified: {preferences.Status} ({preferences.Diagnostic?.Code}).");
        }
        _paths.ValidateDescriptor(candidate);
        return candidate;
    }

    private async Task CopyDirectoryIfPresentAsync(
        DataGenerationDescriptor candidate, string relativePath, CancellationToken cancellationToken)
    {
        string source = GetSourcePath(relativePath);
        if (!TryGetAttributes(source, out FileAttributes attributes)) { return; }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException("A legacy profile directory is occupied by a file.");
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
                string childRelative = Path.GetRelativePath(_legacyRoot, child);
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
            throw new InvalidDataException("A legacy data file is occupied by a directory.");
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
        if ((attributes & FileAttributes.Directory) != 0) { throw new InvalidDataException("A legacy database is occupied by a directory."); }
        ValidateSource(source);
        // SQLite may read these alongside the main database. Reject redirected companions too.
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (TryGetAttributes(source + suffix, out _)) { ValidateSource(source + suffix); }
        }
        string target = GetTargetPath(candidate, relativePath);
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
                throw new InvalidDataException("The migrated database did not pass integrity verification.");
            }
        }
        using FileStream durable = new(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        durable.Flush(flushToDisk: true);
    }

    private string GetSourcePath(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(_legacyRoot, relativePath));
        if (!DataGenerationPathPolicy.IsContainedBy(_legacyRoot, path)) { throw new InvalidDataException("Legacy data path escaped its root."); }
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

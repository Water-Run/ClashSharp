using System.Globalization;
using System.Text;
using ClashSharp.Service;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class LogStorageServiceTests
{
    [Fact]
    public void ExportDatabase_WhenSourceIsCorrupt_PreservesPreviousBackup()
    {
        using TempDatabase database = new();
        LogStorageService service = new(database.Path, () => "profile-a");
        service.AppendLog("Info", "Export", "Previous backup", "detail");
        string destination = Path.Combine(Path.GetDirectoryName(database.Path)!, "backup.sqlite3");
        service.ExportDatabase(destination);
        byte[] originalBackup = File.ReadAllBytes(destination);
        using SqliteConnection poolIdentity = new(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        SqliteConnection.ClearPool(poolIdentity);
        File.WriteAllBytes(database.Path, Encoding.UTF8.GetBytes("invalid SQLite database"));

        Assert.Throws<SqliteException>(() => service.ExportDatabase(destination));

        Assert.Equal(originalBackup, File.ReadAllBytes(destination));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "backup.sqlite3.staging.*"));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public void ExportDatabase_WhenDestinationHasRecoveryFiles_PreservesAllExistingFiles(string suffix)
    {
        using TempDatabase database = new();
        LogStorageService service = new(database.Path, () => "profile-a");
        service.AppendLog("Info", "Export", "Current log", "detail");
        string destination = Path.Combine(Path.GetDirectoryName(database.Path)!, "backup.sqlite3");
        service.ExportDatabase(destination);
        byte[] originalBackup = File.ReadAllBytes(destination);
        byte[] recoveryBytes = Encoding.UTF8.GetBytes("pending destination recovery");
        File.WriteAllBytes(destination + suffix, recoveryBytes);

        Assert.Throws<IOException>(() => service.ExportDatabase(destination));

        Assert.Equal(originalBackup, File.ReadAllBytes(destination));
        Assert.Equal(recoveryBytes, File.ReadAllBytes(destination + suffix));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "backup.sqlite3.staging.*"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public void ExportDatabase_RejectsLiveDatabaseAndRecoveryPaths(string suffix)
    {
        using TempDatabase database = new();
        LogStorageService service = new(database.Path, () => "profile-a");
        service.AppendLog("Info", "Export", "Keep live data", "detail");

        Assert.Throws<ArgumentException>(() => service.ExportDatabase(database.Path + suffix));

        Assert.Equal("Keep live data", Assert.Single(service.GetLogs(20, "Export", "Info")).Message);
    }

    [Fact]
    public void ExportDatabase_WhenDestinationIsLocked_PreservesBackupAndRemovesStaging()
    {
        using TempDatabase database = new();
        LogStorageService service = new(database.Path, () => "profile-a");
        service.AppendLog("Info", "Export", "Previous log", "detail");
        string destination = Path.Combine(Path.GetDirectoryName(database.Path)!, "backup.sqlite3");
        service.ExportDatabase(destination);
        byte[] originalBackup = File.ReadAllBytes(destination);
        service.AppendLog("Info", "Export", "Current log", "detail");
        using FileStream existingReader = new(destination, FileMode.Open, FileAccess.Read, FileShare.Read);

        Exception failure = Assert.ThrowsAny<Exception>(() => service.ExportDatabase(destination));
        Assert.True(failure is IOException or UnauthorizedAccessException);

        Assert.Equal(originalBackup, File.ReadAllBytes(destination));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "backup.sqlite3.staging.*"));
    }

    [Fact]
    public void ExportDatabase_ReplacesBackupWithStandaloneSnapshotIncludingWalData()
    {
        using TempDatabase database = new();
        LogStorageService service = new(database.Path, () => "profile-a");
        service.AppendLog("Info", "Export", "Previous log", "detail");
        string directory = Path.GetDirectoryName(database.Path)!;
        string destination = Path.Combine(directory, "backup.sqlite3");
        service.ExportDatabase(destination);
        using SqliteConnection live = new($"Data Source={database.Path};Pooling=False");
        live.Open();
        using SqliteCommand command = live.CreateCommand();
        command.CommandText = "PRAGMA wal_autocheckpoint=0;";
        command.ExecuteNonQuery();
        service.AppendLog("Info", "Export", "Current WAL log", "detail");
        Assert.True(new FileInfo(database.Path + "-wal").Length > 0);

        service.ExportDatabase(destination);

        string copied = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "standalone")).FullName, "backup.sqlite3");
        File.Copy(destination, copied);
        using SqliteConnection snapshot = new($"Data Source={copied};Mode=ReadOnly;Pooling=False");
        snapshot.Open();
        using SqliteCommand verify = snapshot.CreateCommand();
        verify.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", verify.ExecuteScalar());
        verify.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("delete", verify.ExecuteScalar());
        verify.CommandText = "SELECT COUNT(*) FROM Logs WHERE Source = 'Export';";
        Assert.Equal(2L, Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(copied)!));
        Assert.Empty(Directory.EnumerateFiles(directory, "backup.sqlite3.staging.*"));
    }
}

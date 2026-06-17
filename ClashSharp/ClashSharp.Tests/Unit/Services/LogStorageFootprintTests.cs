/*
 * Log Storage Footprint Tests
 * Verifies SQLite storage footprint calculation for database and WAL sidecar files
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/LogStorageFootprintTests.cs
 * @date: 2026-06-15
 */

using System;
using System.IO;
using System.Linq;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests SQLite storage footprint calculations.</summary>
/// <remarks>
/// Invariants: Tests write only to temporary directories owned by each test.
/// Thread safety: Tests are independent because every test uses a unique directory.
/// Side effects: Creates and deletes temporary files.
/// </remarks>
public sealed class LogStorageFootprintTests
{
    /// <summary>Verifies storage path enumeration includes the database, WAL, and shared-memory files.</summary>
    [Fact]
    public void EnumerateStoragePaths_ReturnsDatabaseAndSidecars()
    {
        const string DatabasePath = "logs.sqlite3";

        string[] paths = LogStorageFootprint.EnumerateStoragePaths(DatabasePath).ToArray();

        Assert.Equal(
            [
                "logs.sqlite3",
                "logs.sqlite3-wal",
                "logs.sqlite3-shm",
            ],
            paths);
    }

    /// <summary>Verifies missing database and sidecar files contribute zero bytes.</summary>
    [Fact]
    public void CalculateBytes_MissingFiles_ReturnsZero()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "logs.sqlite3");

            long bytes = LogStorageFootprint.CalculateBytes(databasePath);

            Assert.Equal(0, bytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies existing database and sidecar sizes are added together.</summary>
    [Fact]
    public void CalculateBytes_DatabaseAndSidecars_ReturnsTotalBytes()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "logs.sqlite3");
            WriteBytes(databasePath, 11);
            WriteBytes(databasePath + "-wal", 13);
            WriteBytes(databasePath + "-shm", 17);

            long bytes = LogStorageFootprint.CalculateBytes(databasePath);

            Assert.Equal(41, bytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Creates a unique temporary directory for one test.</summary>
    /// <returns>Absolute path to an existing temporary directory.</returns>
    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClashSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a deterministic byte count to a file.</summary>
    /// <param name="path">File path to write. Must not be null.</param>
    /// <param name="count">Number of bytes to write; must be zero or greater.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    private static void WriteBytes(string path, int count)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Byte count must be zero or greater.");
        }

        File.WriteAllBytes(path, new byte[count]);
    }
}

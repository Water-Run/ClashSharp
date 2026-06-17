/*
 * SQLite Log Storage Footprint
 * Calculates SQLite database storage usage including WAL-mode sidecar files
 *
 * @author: WaterRun
 * @file: Service/LogStorageFootprint.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.IO;

namespace ClashSharp.Service;

/// <summary>Calculates SQLite database storage usage for log storage files.</summary>
/// <remarks>
/// Invariants: Missing files contribute zero bytes and known WAL-mode sidecar paths are always considered.
/// Thread safety: Stateless methods are safe for concurrent calls.
/// Side effects: Reads file metadata from the filesystem.
/// </remarks>
internal static class LogStorageFootprint
{
    /// <summary>Calculates the SQLite storage footprint including WAL and shared-memory sidecar files.</summary>
    /// <param name="databasePath">Main SQLite database path. Must not be null.</param>
    /// <returns>Total byte count for the main database and known sidecar files.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="databasePath"/> is null.</exception>
    public static long CalculateBytes(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);

        long totalBytes = 0;
        foreach (string path in EnumerateStoragePaths(databasePath))
        {
            if (File.Exists(path))
            {
                totalBytes += new FileInfo(path).Length;
            }
        }

        return totalBytes;
    }

    /// <summary>Enumerates the main SQLite database path and WAL-mode sidecar paths.</summary>
    /// <param name="databasePath">Main SQLite database path. Must not be null.</param>
    /// <returns>Known storage paths for this database.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="databasePath"/> is null.</exception>
    public static IEnumerable<string> EnumerateStoragePaths(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);

        yield return databasePath;
        yield return databasePath + "-wal";
        yield return databasePath + "-shm";
    }
}

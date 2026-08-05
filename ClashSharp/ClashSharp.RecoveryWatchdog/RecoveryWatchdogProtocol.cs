using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Service;

namespace ClashSharp.Recovery;

internal sealed record RecoveryWatchdogLease(
    int SchemaVersion,
    Guid Nonce,
    int ParentProcessId,
    long ParentStartTimeUtcTicks)
{
    internal const int CurrentSchemaVersion = 1;

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion
            || Nonce == Guid.Empty
            || ParentProcessId <= 0
            || ParentStartTimeUtcTicks <= 0)
        {
            throw new InvalidDataException("The recovery watchdog lease is invalid.");
        }
    }
}

internal readonly record struct RecoveryWatchdogInvocation(
    Guid Nonce,
    int ParentProcessId,
    long ParentStartTimeUtcTicks)
{
    internal static RecoveryWatchdogInvocation Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 6)
        {
            throw new ArgumentException("Recovery watchdog arguments are incomplete.", nameof(arguments));
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index += 2)
        {
            if (!values.TryAdd(arguments[index], arguments[index + 1]))
            {
                throw new ArgumentException("Recovery watchdog arguments contain a duplicate option.", nameof(arguments));
            }
        }

        if (!values.TryGetValue("--nonce", out string? nonceText)
            || !Guid.TryParseExact(nonceText, "N", out Guid nonce)
            || nonce == Guid.Empty
            || !TryParsePositive(values, "--parent-pid", out int parentProcessId)
            || !TryParsePositive(values, "--parent-start-utc-ticks", out long parentStartTimeUtcTicks))
        {
            throw new ArgumentException("Recovery watchdog arguments are invalid.", nameof(arguments));
        }

        return new RecoveryWatchdogInvocation(nonce, parentProcessId, parentStartTimeUtcTicks);
    }

    internal RecoveryWatchdogLease ToLease() => new(
        RecoveryWatchdogLease.CurrentSchemaVersion,
        Nonce,
        ParentProcessId,
        ParentStartTimeUtcTicks);

    internal void AddTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.ArgumentList.Add("--nonce");
        startInfo.ArgumentList.Add(Nonce.ToString("N"));
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(ParentProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--parent-start-utc-ticks");
        startInfo.ArgumentList.Add(ParentStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryParsePositive<T>(
        IReadOnlyDictionary<string, string> values,
        string name,
        out T result)
        where T : struct, INumber<T>
    {
        result = T.Zero;
        return values.TryGetValue(name, out string? text)
            && T.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result > T.Zero;
    }
}

internal static class RecoveryWatchdogPaths
{
    internal const string LeaseFileName = "RecoveryWatchdogLease.json";
    internal const string LockFileName = "RecoveryWatchdog.lock";
    internal const string InstallerMutationLockFileName = "InstallerMutation.lock";
    internal const string ProxyJournalFileName = "WindowsProxyMutationJournal.json";

    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    internal static string ResolveLocalDataDirectory()
    {
        int length = 0;
        int result = GetCurrentPackageFamilyName(ref length, null);
        if (result == ErrorInsufficientBuffer && length > 1)
        {
            char[] familyName = new char[length];
            result = GetCurrentPackageFamilyName(ref length, familyName);
            if (result == 0 && length > 1)
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages",
                    new string(familyName, 0, length - 1),
                    "LocalState");
            }
        }

        if (result != AppModelErrorNoPackage)
        {
            throw new InvalidOperationException($"Could not resolve the current package family (Win32 {result}).");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashSharp");
    }

    /// <summary>
    /// Resolves the package-independent per-user lock that prevents App startup while the
    /// Installer is deploying or removing the package itself.
    /// </summary>
    internal static string ResolveInstallerMutationLockPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashSharp",
            InstallerMutationLockFileName);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(
        ref int packageFamilyNameLength,
        [Out] char[]? packageFamilyName);
}

internal sealed class RecoveryWatchdogLeaseFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    internal RecoveryWatchdogLeaseFileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    internal RecoveryWatchdogLease? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            RecoveryWatchdogLease lease = JsonSerializer.Deserialize<RecoveryWatchdogLease>(
                File.ReadAllText(_path),
                JsonOptions) ?? throw new InvalidDataException("The recovery watchdog lease is empty.");
            lease.Validate();
            return lease;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The recovery watchdog lease is invalid.", exception);
        }
    }

    internal void Write(RecoveryWatchdogLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.Validate();
        DurableAtomicFile.WriteText(_path, JsonSerializer.Serialize(lease, JsonOptions));
    }

    internal void ClearIfMatches(RecoveryWatchdogLease expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (Read() == expected)
        {
            File.Delete(_path);
        }
    }
}

internal static class RecoveryWatchdogFileLock
{
    internal static async Task<FileStream?> TryAcquireAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The recovery lock directory could not be resolved."));
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    fullPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}

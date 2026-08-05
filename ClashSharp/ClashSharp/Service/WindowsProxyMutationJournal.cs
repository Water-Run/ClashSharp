using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ClashSharp.Service;

/// <summary>Presence and value of one DWORD WinINet setting.</summary>
internal readonly record struct WindowsProxyDwordValue(bool Exists, int Value);

/// <summary>Supported registry storage kind for one WinINet string setting.</summary>
internal enum WindowsProxyStringKind
{
    None,
    String,
    ExpandString,
}

/// <summary>Presence, exact value, and storage kind of one string WinINet setting.</summary>
internal readonly record struct WindowsProxyStringValue(
    bool Exists,
    string? Value,
    WindowsProxyStringKind Kind = WindowsProxyStringKind.String);

/// <summary>Complete WinINet tuple owned and restored by Clash# system-proxy transitions.</summary>
internal sealed record WindowsProxyRegistrySnapshot(
    WindowsProxyDwordValue ProxyEnable,
    WindowsProxyStringValue ProxyServer,
    WindowsProxyStringValue ProxyOverride,
    WindowsProxyStringValue AutoConfigUrl);

/// <summary>Durable phase of one WinINet ownership transition.</summary>
internal enum WindowsProxyMutationPhase
{
    /// <summary>The applied tuple was completely written and finalized.</summary>
    Applied,

    /// <summary>The pending tuple was journaled before its fields were written.</summary>
    Applying,
}

/// <summary>Reads and atomically writes the complete per-user WinINet proxy tuple.</summary>
internal interface IWindowsProxyRegistryStore
{
    WindowsProxyRegistrySnapshot Read();

    void Write(WindowsProxyRegistrySnapshot snapshot);
}

/// <summary>Durable baseline, prior applied tuple, and optional pending tuple.</summary>
/// <remarks>
/// During <see cref="WindowsProxyMutationPhase.Applying"/>, <see cref="Applied"/> is the exact
/// pre-write tuple and <see cref="PendingApplied"/> is the intended tuple. File presence remains
/// the ownership marker, so either value can safely authorize per-field restoration after a crash.
/// </remarks>
internal sealed record WindowsProxyMutationJournal(
    int SchemaVersion,
    WindowsProxyRegistrySnapshot Baseline,
    WindowsProxyRegistrySnapshot Applied,
    WindowsProxyMutationPhase Phase = WindowsProxyMutationPhase.Applied,
    WindowsProxyRegistrySnapshot? PendingApplied = null)
{
    public const int CurrentSchemaVersion = 2;

    internal const int LegacyAppliedOnlySchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported Windows proxy journal schema version: {SchemaVersion}.");
        }

        ValidateSnapshot(Baseline);
        ValidateSnapshot(Applied);

        if (!Enum.IsDefined(Phase)
            || Phase == WindowsProxyMutationPhase.Applied && PendingApplied is not null
            || Phase == WindowsProxyMutationPhase.Applying && PendingApplied is null)
        {
            throw new InvalidDataException("Windows proxy journal contains an invalid mutation phase.");
        }

        if (PendingApplied is not null)
        {
            ValidateSnapshot(PendingApplied);
        }
    }

    private static void ValidateSnapshot(WindowsProxyRegistrySnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new InvalidDataException("Windows proxy journal snapshot is missing.");
        }

        if (!snapshot.ProxyEnable.Exists && snapshot.ProxyEnable.Value != 0
            || snapshot.ProxyServer.Exists != (snapshot.ProxyServer.Value is not null)
            || snapshot.ProxyOverride.Exists != (snapshot.ProxyOverride.Value is not null)
            || snapshot.AutoConfigUrl.Exists != (snapshot.AutoConfigUrl.Value is not null)
            || !HasValidKind(snapshot.ProxyServer)
            || !HasValidKind(snapshot.ProxyOverride)
            || !HasValidKind(snapshot.AutoConfigUrl))
        {
            throw new InvalidDataException("Windows proxy journal contains an invalid string value.");
        }
    }

    private static bool HasValidKind(WindowsProxyStringValue value)
    {
        return value.Exists
            ? value.Kind is WindowsProxyStringKind.String or WindowsProxyStringKind.ExpandString
            : value.Kind == WindowsProxyStringKind.None;
    }
}

/// <summary>Persists the Windows proxy ownership journal separately from diagnostic repair state.</summary>
internal interface IWindowsProxyMutationJournalStore
{
    WindowsProxyMutationJournal? Read();

    void Write(WindowsProxyMutationJournal journal);

    void Clear();
}

/// <summary>Atomic local-file implementation of the Windows proxy ownership journal.</summary>
internal sealed class WindowsProxyMutationJournalFileStore : IWindowsProxyMutationJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _journalPath;

    public WindowsProxyMutationJournalFileStore(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        _journalPath = Path.GetFullPath(journalPath);
    }

    public WindowsProxyMutationJournal? Read()
    {
        if (!File.Exists(_journalPath))
        {
            return null;
        }

        try
        {
            WindowsProxyMutationJournal journal = JsonSerializer.Deserialize<WindowsProxyMutationJournal>(
                File.ReadAllText(_journalPath),
                JsonOptions) ?? throw new InvalidDataException("Windows proxy journal is empty.");
            if (journal.SchemaVersion == WindowsProxyMutationJournal.LegacyAppliedOnlySchemaVersion)
            {
                journal = journal with { SchemaVersion = WindowsProxyMutationJournal.CurrentSchemaVersion };
            }

            journal.Validate();
            return journal;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Windows proxy journal is invalid.", exception);
        }
    }

    public void Write(WindowsProxyMutationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        DurableAtomicFile.WriteText(
            _journalPath,
            JsonSerializer.Serialize(journal, JsonOptions));
    }

    public void Clear()
    {
        File.Delete(_journalPath);
    }
}

/// <summary>Production registry implementation for the complete per-user WinINet proxy tuple.</summary>
internal sealed class WindowsProxyRegistryStore(Action notifySettingsChanged) : IWindowsProxyRegistryStore
{
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string ProxyEnableValueName = "ProxyEnable";
    private const string ProxyServerValueName = "ProxyServer";
    private const string ProxyOverrideValueName = "ProxyOverride";
    private const string AutoConfigUrlValueName = "AutoConfigURL";

    private readonly Action _notifySettingsChanged = notifySettingsChanged
        ?? throw new ArgumentNullException(nameof(notifySettingsChanged));

    public WindowsProxyRegistrySnapshot Read()
    {
        using RegistryKey key = OpenInternetSettingsKey(writable: false);
        return new WindowsProxyRegistrySnapshot(
            ReadDword(key, ProxyEnableValueName),
            ReadString(key, ProxyServerValueName),
            ReadString(key, ProxyOverrideValueName),
            ReadString(key, AutoConfigUrlValueName));
    }

    public void Write(WindowsProxyRegistrySnapshot snapshot)
    {
        using RegistryKey key = OpenInternetSettingsKey(writable: true);
        WriteDword(key, ProxyEnableValueName, snapshot.ProxyEnable);
        WriteString(key, ProxyServerValueName, snapshot.ProxyServer);
        WriteString(key, ProxyOverrideValueName, snapshot.ProxyOverride);
        WriteString(key, AutoConfigUrlValueName, snapshot.AutoConfigUrl);
        _notifySettingsChanged();
    }

    private static WindowsProxyDwordValue ReadDword(RegistryKey key, string name)
    {
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => new WindowsProxyDwordValue(false, 0),
            int number when key.GetValueKind(name) == RegistryValueKind.DWord => new WindowsProxyDwordValue(true, number),
            _ => throw new InvalidDataException($"WinINet value '{name}' is not a DWORD."),
        };
    }

    private static WindowsProxyStringValue ReadString(RegistryKey key, string name)
    {
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => new WindowsProxyStringValue(false, null, WindowsProxyStringKind.None),
            string text when key.GetValueKind(name) == RegistryValueKind.String =>
                new WindowsProxyStringValue(true, text, WindowsProxyStringKind.String),
            string text when key.GetValueKind(name) == RegistryValueKind.ExpandString =>
                new WindowsProxyStringValue(true, text, WindowsProxyStringKind.ExpandString),
            _ => throw new InvalidDataException($"WinINet value '{name}' is not a string."),
        };
    }

    private static void WriteDword(RegistryKey key, string name, WindowsProxyDwordValue value)
    {
        if (value.Exists)
        {
            key.SetValue(name, value.Value, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static void WriteString(RegistryKey key, string name, WindowsProxyStringValue value)
    {
        if (value.Exists)
        {
            RegistryValueKind kind = value.Kind switch
            {
                WindowsProxyStringKind.String => RegistryValueKind.String,
                WindowsProxyStringKind.ExpandString => RegistryValueKind.ExpandString,
                _ => throw new InvalidDataException($"WinINet value '{name}' has an invalid string kind."),
            };
            key.SetValue(name, value.Value!, kind);
        }
        else
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static RegistryKey OpenInternetSettingsKey(bool writable)
    {
        return Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable)
            ?? throw new InvalidOperationException("Windows Internet Settings registry key could not be opened.");
    }
}

/// <summary>Shared durable atomic text writer used by Windows ownership journals.</summary>
internal static class DurableAtomicFile
{
    public static void WriteText(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Journal directory could not be resolved.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content + "\n");
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

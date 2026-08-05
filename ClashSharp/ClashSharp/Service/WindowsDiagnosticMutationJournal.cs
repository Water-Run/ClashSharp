using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClashSharp.Service;

/// <summary>Persists Windows diagnostic mutation ownership and pre-mutation values.</summary>
internal interface IWindowsDiagnosticMutationJournalStore
{
    /// <summary>Reads the current durable mutation journal.</summary>
    WindowsDiagnosticMutationJournal Read();

    /// <summary>Atomically replaces the durable mutation journal.</summary>
    void Write(WindowsDiagnosticMutationJournal journal);
}

/// <summary>Identifies the diagnostic action currently owning an environment mutation.</summary>
[Flags]
internal enum WindowsDiagnosticMutationOwner
{
    None = 0,
    Terminal = 1,
    Wsl = 2,
}

/// <summary>Durable phase of one Windows diagnostic mutation.</summary>
internal enum WindowsDiagnosticMutationPhase
{
    /// <summary>The applied value was completely written and finalized.</summary>
    Applied,

    /// <summary>The pending value was journaled before the external write.</summary>
    Applying,
}

/// <summary>Baseline, prior/pending applied values, and owners for one user environment variable.</summary>
internal sealed record WindowsDiagnosticEnvironmentMutation(
    bool BaselineExists,
    string? BaselineValue,
    string? AppliedValue,
    WindowsDiagnosticMutationOwner Owners,
    WindowsDiagnosticMutationPhase Phase = WindowsDiagnosticMutationPhase.Applied,
    string? PendingAppliedValue = null);

/// <summary>Baseline and prior/pending Microsoft Store loopback exemption state.</summary>
internal sealed record WindowsDiagnosticStoreMutation(
    bool BaselinePresent,
    bool AppliedPresent,
    WindowsDiagnosticMutationPhase Phase = WindowsDiagnosticMutationPhase.Applied,
    bool? PendingAppliedPresent = null);

/// <summary>Versioned durable state required to undo Windows mutations without overwriting external changes.</summary>
internal sealed record WindowsDiagnosticMutationJournal(
    int SchemaVersion,
    Dictionary<string, WindowsDiagnosticEnvironmentMutation> EnvironmentVariables,
    WindowsDiagnosticStoreMutation? MicrosoftStore)
{
    /// <summary>Current on-disk schema version.</summary>
    public const int CurrentSchemaVersion = 2;

    internal const int LegacyAppliedOnlySchemaVersion = 1;

    /// <summary>Creates an empty journal.</summary>
    public static WindowsDiagnosticMutationJournal Empty()
    {
        return new WindowsDiagnosticMutationJournal(
            CurrentSchemaVersion,
            new Dictionary<string, WindowsDiagnosticEnvironmentMutation>(StringComparer.OrdinalIgnoreCase),
            null);
    }

    /// <summary>Gets whether no mutations remain owned by Clash#.</summary>
    public bool IsEmpty => EnvironmentVariables.Count == 0 && MicrosoftStore is null;

    /// <summary>Validates deserialized state before it can authorize a restore.</summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported Windows mutation journal schema version: {SchemaVersion}.");
        }

        if (EnvironmentVariables is null)
        {
            throw new InvalidDataException("Windows mutation journal environment state is missing.");
        }

        foreach ((string name, WindowsDiagnosticEnvironmentMutation? mutation) in EnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(name)
                || mutation is null
                || mutation.Owners == WindowsDiagnosticMutationOwner.None
                || (mutation.Owners & ~(WindowsDiagnosticMutationOwner.Terminal | WindowsDiagnosticMutationOwner.Wsl)) != 0)
            {
                throw new InvalidDataException("Windows mutation journal contains an invalid environment entry.");
            }

            if (mutation.BaselineExists != (mutation.BaselineValue is not null)
                || !Enum.IsDefined(mutation.Phase)
                || mutation.Phase == WindowsDiagnosticMutationPhase.Applied
                    && (mutation.AppliedValue is null || mutation.PendingAppliedValue is not null)
                || mutation.Phase == WindowsDiagnosticMutationPhase.Applying
                    && mutation.PendingAppliedValue is null)
            {
                throw new InvalidDataException("Windows mutation journal contains an invalid environment value or phase.");
            }
        }

        if (MicrosoftStore is { } store
            && (!Enum.IsDefined(store.Phase)
                || store.Phase == WindowsDiagnosticMutationPhase.Applied && store.PendingAppliedPresent is not null
                || store.Phase == WindowsDiagnosticMutationPhase.Applying && store.PendingAppliedPresent is null))
        {
            throw new InvalidDataException("Windows mutation journal contains an invalid Microsoft Store phase.");
        }
    }
}

/// <summary>Stores the Windows mutation journal as a local, non-exported JSON file.</summary>
internal sealed class WindowsDiagnosticMutationJournalFileStore : IWindowsDiagnosticMutationJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _journalPath;

    /// <summary>Initializes a journal store at an absolute local-data path.</summary>
    public WindowsDiagnosticMutationJournalFileStore(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        _journalPath = Path.GetFullPath(journalPath);
    }

    /// <inheritdoc />
    public WindowsDiagnosticMutationJournal Read()
    {
        if (!File.Exists(_journalPath))
        {
            return WindowsDiagnosticMutationJournal.Empty();
        }

        try
        {
            string json = File.ReadAllText(_journalPath);
            WindowsDiagnosticMutationJournal journal = JsonSerializer.Deserialize<WindowsDiagnosticMutationJournal>(json, JsonOptions)
                ?? throw new InvalidDataException("Windows mutation journal is empty.");
            if (journal.SchemaVersion == WindowsDiagnosticMutationJournal.LegacyAppliedOnlySchemaVersion)
            {
                journal = journal with { SchemaVersion = WindowsDiagnosticMutationJournal.CurrentSchemaVersion };
            }

            journal.Validate();
            return journal with
            {
                EnvironmentVariables = new Dictionary<string, WindowsDiagnosticEnvironmentMutation>(
                    journal.EnvironmentVariables,
                    StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Windows mutation journal is invalid.", exception);
        }
    }

    /// <inheritdoc />
    public void Write(WindowsDiagnosticMutationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();

        if (journal.IsEmpty)
        {
            File.Delete(_journalPath);
            return;
        }

        DurableAtomicFile.WriteText(
            _journalPath,
            JsonSerializer.Serialize(journal, JsonOptions));
    }
}

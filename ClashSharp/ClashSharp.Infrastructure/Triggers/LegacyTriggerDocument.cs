using System.Collections.ObjectModel;
using System.Text.Json;

namespace ClashSharp.Infrastructure.Triggers;

internal sealed class LegacyTriggerDocument
{
    public LegacyTriggerDocument(
        string sourceHash,
        IEnumerable<JsonElement> tasks,
        string? documentErrorCode)
    {
        SourceHash = sourceHash;
        Tasks = Array.AsReadOnly(tasks.Select(task => task.Clone()).ToArray());
        DocumentErrorCode = documentErrorCode;
    }

    public string SourceHash { get; }

    public ReadOnlyCollection<JsonElement> Tasks { get; }

    public string? DocumentErrorCode { get; }

    public bool IsValidShape => DocumentErrorCode is null;
}

internal sealed record LegacyTaskQuarantine(
    int Index,
    string ErrorCode,
    string RawJson);

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Service;

/// <summary>Validated package contents detached from the selected file, with no writes or runtime effects.</summary>
internal sealed record DataPackageImportPlan(ClashDataPackageScope Scope,
    IReadOnlyList<SettingValueChange> Settings, IReadOnlyList<DataPackageImportFile> Files);

internal sealed record DataPackageImportFile(string RelativePath, ReadOnlyMemory<byte> Content);

internal sealed partial class ClashDataPackageService
{
    /// <summary>Validates the existing format and snapshots all accepted contents before candidate allocation.</summary>
    internal DataPackageImportPlan ReadImportPlan(string packagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = ValidatePackageRoot(LoadBoundedPackage(packagePath));
        ClashDataPackageScope scope = ParsePackageScope(root);
        IReadOnlyList<ImportFilePayload> files = BuildImportFilePayloads(root.Element("Files"), scope, cancellationToken);
        IReadOnlyDictionary<string, string> settings = ValidateSettings(root.Element("Settings"));
        List<SettingValueChange> changes = [];
        foreach ((string key, string value) in settings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SettingDefinition definition = SettingsRegistry.Default.Get(key);
            // Version-one packages spell Boolean values as True/False; the authority's
            // durable text is lowercase. Convert the validated legacy value through its type.
            SettingNormalizationResult normalized = definition.ValueType == typeof(bool)
                ? definition.NormalizeValue(bool.Parse(value))
                : definition.Normalize(value);
            if (!normalized.IsSuccess) { throw new InvalidDataException($"Package preference '{key}' is invalid."); }
            changes.Add(new(definition.Key, normalized.Value!));
        }
        return new(scope, changes.AsReadOnly(), Array.AsReadOnly(files.Select(file =>
            new DataPackageImportFile(Path.GetRelativePath(_localDataDirectory, file.TargetPath), file.Content)).ToArray()));
    }
}

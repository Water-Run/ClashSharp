using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Pins backup settings and files to the same current generation through destination publication.</summary>
internal sealed class GenerationDataPackageExporter(
    DataGenerationManager generations, MutationAdmissionBarrier admission, string applicationDataRoot) : IDataPackageExporter
{
    private readonly string _applicationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDataRoot));

    public Task ExportAdmittedAsync(string packagePath, ClashDataPackageScope scope,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        admission.EnsureActiveExclusiveLease(admissionLease);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        string destination = Path.GetFullPath(packagePath);
        if (DataGenerationPathPolicy.IsContainedBy(_applicationRoot, destination))
        {
            throw new InvalidOperationException("A backup cannot overwrite application data. Choose another folder.");
        }
        DataGenerationPathPolicy.ValidateNoReparsePoints(destination, GetExistingAttributes);
        return generations.ExecuteAsync<SettingsAuthoritySession>(
            (session, descriptor, token) => new ClashDataPackageService(
                new DataPackageSettingsSnapshot(session.Snapshot), descriptor.RootPath)
                .ExportAsync(destination, scope, token), cancellationToken);
    }

    private static FileAttributes GetExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        // Missing leaves may be created; the path walker still inspects every existing ancestor.
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }
}

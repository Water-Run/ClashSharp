using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Exports a consistent user backup while the caller owns exclusive mutation admission.</summary>
internal interface IDataPackageExporter
{
    Task ExportAdmittedAsync(string packagePath, ClashDataPackageScope scope,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken);
}

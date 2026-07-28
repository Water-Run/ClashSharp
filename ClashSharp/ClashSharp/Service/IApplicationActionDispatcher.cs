using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Boundary for dispatching application actions without coupling callers to concrete UI pages.</summary>
internal interface IApplicationActionDispatcher
{
    Task DispatchAsync(ApplicationActionKind kind, string value, CancellationToken cancellationToken);
}

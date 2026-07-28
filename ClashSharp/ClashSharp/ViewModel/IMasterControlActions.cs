using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Application-action boundary required by master-control functional tiles.</summary>
internal interface IMasterControlActions
{
    Task DispatchAsync(
        ApplicationActionKind kind,
        string value,
        CancellationToken cancellationToken);
}

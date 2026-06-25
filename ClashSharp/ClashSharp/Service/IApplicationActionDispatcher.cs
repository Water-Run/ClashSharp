/*
 * Application Action Dispatcher
 * Shared boundary for actions requested by tiles, triggers, and traditional UI entry points
 *
 * @author: WaterRun
 * @file: Service/IApplicationActionDispatcher.cs
 * @date: 2026-06-26
 */

using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Boundary for dispatching application actions without coupling callers to concrete UI pages.</summary>
internal interface IApplicationActionDispatcher
{
    Task DispatchAsync(ApplicationActionKind kind, string value, CancellationToken cancellationToken);
}

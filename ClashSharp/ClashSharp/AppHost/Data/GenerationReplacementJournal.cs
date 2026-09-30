using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Hosting.Data;

/// <summary>Durable evidence retained until the selected data directory and its native runtime are verified.</summary>
internal sealed record GenerationReplacementCheckpoint(int Version, long Revision, Guid OperationId,
    DataGenerationManifestSnapshot Baseline, GenerationExternalStateSnapshot External,
    DataGenerationDescriptor? Candidate, bool Completed);

internal interface IGenerationReplacementJournal
{
    Task<GenerationReplacementCheckpoint?> ReadPendingAsync(CancellationToken cancellationToken);
    Task BeginAsync(Guid operationId, DataGenerationManifestSnapshot baseline, GenerationExternalStateSnapshot external, CancellationToken cancellationToken);
    Task SetCandidateAsync(Guid operationId, DataGenerationDescriptor candidate, CancellationToken cancellationToken);
    Task CompleteAsync(Guid operationId, CancellationToken cancellationToken);
}

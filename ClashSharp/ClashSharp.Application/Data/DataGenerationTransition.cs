namespace ClashSharp.ApplicationModel.Data;

/// <summary>Owns one drained generation transition until explicit commit, rollback, or abort.</summary>
public sealed partial class DataGenerationTransition : IAsyncDisposable
{
    private readonly DataGenerationScope _baselineScope;
    private DataGenerationManager? _owner;
    private DataGenerationScope? _stagedScope;
    private DataGenerationManifestSnapshot? _promotedManifest;
    private DataGenerationManifestSnapshot? _restoredManifest;
    private int _isSwapped;
    private int _resolution;

    internal DataGenerationTransition(
        DataGenerationManager owner,
        DataGenerationManifestSnapshot baselineManifest,
        DataGenerationScope baselineScope)
    {
        _owner = owner;
        BaselineManifest = baselineManifest;
        _baselineScope = baselineScope;
    }

    /// <summary>Gets the verified manifest that was authoritative when drain began.</summary>
    public DataGenerationManifestSnapshot BaselineManifest { get; }

    /// <summary>Gets the current state of the baseline scope.</summary>
    public DataGenerationScopeState BaselineScopeState => _baselineScope.State;

    /// <summary>Gets the staged descriptor, or null before staging.</summary>
    public DataGenerationDescriptor? StagedDescriptor =>
        Volatile.Read(ref _stagedScope)?.Descriptor;

    /// <summary>Gets whether durable manifest promotion has been acknowledged.</summary>
    public bool IsManifestPromoted => Volatile.Read(ref _promotedManifest) is not null;

    /// <summary>Gets whether the in-memory current scope has been swapped.</summary>
    public bool IsSwapped => Volatile.Read(ref _isSwapped) != 0;

    /// <summary>Gets whether the explicit commit cut has made this transition forward-only.</summary>
    public bool IsCommitted => Volatile.Read(ref _resolution) == ResolutionCommitted;

    /// <summary>Gets the verified promoted manifest, or null before durable promotion is known.</summary>
    public DataGenerationManifestSnapshot? PromotedManifest =>
        Volatile.Read(ref _promotedManifest);

    internal DataGenerationScope BaselineScope => _baselineScope;

    internal DataGenerationScope? StagedScope => _stagedScope;

    internal bool IsResolved => Volatile.Read(ref _resolution) != ResolutionPending;

    internal bool IsRolledBack => Volatile.Read(ref _resolution) == ResolutionRolledBack;

    internal bool IsAborted => Volatile.Read(ref _resolution) == ResolutionAborted;

    internal DataGenerationManifestSnapshot? RestoredManifest => _restoredManifest;

    /// <summary>Transfers ownership of one paused candidate scope into this transition.</summary>
    /// <param name="scope">Prepared scope that must remain invisible until swap.</param>
    public void Stage(DataGenerationScope scope)
    {
        GetOwner().Stage(this, scope);
    }

    /// <summary>Records the verified durable promotion separately from the in-memory swap.</summary>
    /// <param name="promotedManifest">Manifest returned by the durable store.</param>
    public void AcknowledgeManifestPromotion(DataGenerationManifestSnapshot promotedManifest)
    {
        GetOwner().AcknowledgeManifestPromotion(this, promotedManifest);
    }

    /// <summary>Atomically changes the in-memory current scope to the promoted candidate.</summary>
    public void SwapToPromoted()
    {
        GetOwner().SwapToPromoted(this);
    }

    /// <summary>Commits the swapped scope and disposes the old scope.</summary>
    public async ValueTask CommitAsync()
    {
        DataGenerationManager owner = GetOwner();
        await owner.CommitAsync(this).ConfigureAwait(false);
        Interlocked.Exchange(ref _owner, null);
    }

    /// <summary>Aborts before durable promotion and disposes any staged candidate.</summary>
    public async ValueTask AbortAsync()
    {
        DataGenerationManager owner = GetOwner();
        await owner.AbortAsync(this, observedBaseline: null).ConfigureAwait(false);
        Interlocked.Exchange(ref _owner, null);
    }

    /// <summary>Safely aborts an unpromoted transition; promoted work requires explicit resolution.</summary>
    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _owner) is null)
        {
            return ValueTask.CompletedTask;
        }

        if (IsCommitted)
        {
            return CommitAsync();
        }

        if (IsRolledBack)
        {
            return RetryRollbackCleanupAsync();
        }

        if (IsAborted || StagedScope is null)
        {
            return AbortAsync();
        }

        return ValueTask.FromException(new DataGenerationManagerException(
                DataGenerationManagerError.ManifestPromotionUncertain,
                "A staged transition requires store-backed abort, rollback, or commit."));
    }

    internal void SetStagedScope(DataGenerationScope scope)
    {
        Volatile.Write(ref _stagedScope, scope);
    }

    internal void SetPromotedManifest(DataGenerationManifestSnapshot snapshot)
    {
        Volatile.Write(ref _promotedManifest, snapshot);
    }

    internal void MarkSwapped()
    {
        Volatile.Write(ref _isSwapped, 1);
    }

    internal void MarkCommitted()
    {
        Volatile.Write(ref _resolution, ResolutionCommitted);
    }

    internal void MarkRolledBack(DataGenerationManifestSnapshot restoredManifest)
    {
        Volatile.Write(ref _restoredManifest, restoredManifest);
        Volatile.Write(ref _resolution, ResolutionRolledBack);
    }

    internal void MarkAborted()
    {
        Volatile.Write(ref _resolution, ResolutionAborted);
    }

    internal void DetachOwner()
    {
        Interlocked.Exchange(ref _owner, null);
    }

    private DataGenerationManager GetOwner()
    {
        return Volatile.Read(ref _owner)
            ?? throw new ObjectDisposedException(nameof(DataGenerationTransition));
    }

    private async ValueTask RetryRollbackCleanupAsync()
    {
        DataGenerationManager owner = GetOwner();
        await owner
            .RollbackAsync(this, RestoredManifest!)
            .ConfigureAwait(false);
        Interlocked.Exchange(ref _owner, null);
    }

    private const int ResolutionPending = 0;
    private const int ResolutionCommitted = 1;
    private const int ResolutionRolledBack = 2;
    private const int ResolutionAborted = 3;
}

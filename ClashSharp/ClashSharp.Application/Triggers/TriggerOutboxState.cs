namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Identifies durable action reconciliation state.</summary>
public enum TriggerOutboxState
{
    /// <summary>The action is durably queued and has not started.</summary>
    Pending = 0,

    /// <summary>The action may have begun and requires final-state reconciliation after interruption.</summary>
    Running = 1,

    /// <summary>Ownership was durably handed to the outer lifetime protocol.</summary>
    HandedOff = 2,

    /// <summary>The desired effect was verified.</summary>
    Succeeded = 3,

    /// <summary>The effect failed conclusively and may follow its retry policy.</summary>
    Failed = 4,

    /// <summary>The effect cannot be queried or deduplicated; later actions are blocked.</summary>
    Uncertain = 5,
}

/// <summary>Identifies aggregate execution state.</summary>
public enum TriggerExecutionState
{
    /// <summary>The complete outbox is durably pending.</summary>
    Pending = 0,

    /// <summary>At least one action is being reconciled.</summary>
    Running = 1,

    /// <summary>Exit ownership was handed to the outer lifetime.</summary>
    HandedOff = 2,

    /// <summary>Every action succeeded.</summary>
    Succeeded = 3,

    /// <summary>An action failed conclusively.</summary>
    Failed = 4,

    /// <summary>An action effect is uncertain and blocks progress.</summary>
    Uncertain = 5,
}

/// <summary>Identifies durable exit-handoff progress.</summary>
public enum TriggerLifecycleHandoffState
{
    /// <summary>The lifecycle request was durably inserted and published.</summary>
    HandedOff = 0,

    /// <summary>Every host-owned trigger lease was released.</summary>
    ReleaseAcknowledged = 1,

    /// <summary>The App-owned runner began shutdown.</summary>
    ShutdownStarted = 2,

    /// <summary>The requested lifecycle outcome completed.</summary>
    Succeeded = 3,

    /// <summary>The requested lifecycle outcome failed conclusively.</summary>
    Failed = 4,

    /// <summary>The lifecycle outcome cannot be verified safely.</summary>
    Uncertain = 5,
}

namespace Jellyfin.Plugin.ThemeForge.Engines.Index;

/// <summary>
/// Where an item stands in the pipeline. The index is the only authority on this;
/// re-runs consult it before doing any work, which is what makes the whole thing idempotent.
/// </summary>
public enum ThemeItemState
{
    /// <summary>Never looked at.</summary>
    Unprocessed = 0,

    /// <summary>Selected for the next run.</summary>
    Queued = 1,

    /// <summary>A candidate cleared the auto-assign threshold and was written to the library.</summary>
    AutoAssigned = 2,

    /// <summary>A candidate scored well enough to offer but not to assign; waiting on a human.</summary>
    PendingReview = 3,

    /// <summary>A human approved a candidate from the review queue.</summary>
    Approved = 4,

    /// <summary>A human rejected every candidate offered; do not offer them again.</summary>
    Rejected = 5,

    /// <summary>Searching or acquiring failed; eligible for retry once the backoff expires.</summary>
    Failed = 6,

    /// <summary>A human assigned this theme by hand. Never overwritten.</summary>
    ManualOverride = 7,

    /// <summary>Pinned by a human. Never touched again by any automated run.</summary>
    Locked = 8,
}

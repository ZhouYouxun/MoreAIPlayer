namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// The single unified result every automation task/goal returns each tick (design §13.1). The
/// planner and watchdogs branch on this instead of on ad-hoc booleans, so a stuck goal always has
/// a diagnosable reason rather than silently spinning.
/// </summary>
public enum AutoTaskResult
{
    /// <summary>A legal action was taken this tick and the goal is progressing.</summary>
    Running,

    /// <summary>The goal's success predicate is satisfied; the planner may move on.</summary>
    Succeeded,

    /// <summary>A temporary condition (weather, time, an NPC, a boss, a drop) is not met yet.</summary>
    RetryLater,

    /// <summary>The target exists but the current route has made no progress for too long; the
    /// planner should try another route or another goal and come back later.</summary>
    AlternativeRequired,

    /// <summary>A full-world check confirmed the critical resource does not exist and has no legal
    /// substitute.</summary>
    BlockedMissingWorldResource,

    /// <summary>A special world seed has no strategy provider yet.</summary>
    BlockedUnsupportedWorld,

    /// <summary>A mod replaced a vanilla gate and there is no automation provider for it.</summary>
    BlockedUnsupportedMod,

    /// <summary>Code faulted; handed to the fault guard. The channel is disabled after repeats.</summary>
    Faulted
}

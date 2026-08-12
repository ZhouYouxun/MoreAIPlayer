using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Why the automation companion is currently blocked or waiting, for diagnostics and the
/// watchdog's escalation. Distinct from <see cref="AutoTaskResult"/>: a result is this tick's
/// outcome; a reason is the persistent explanation the watchdog settled on.</summary>
public enum AutoBlockReason : byte
{
    None,
    /// <summary>No task progress for the goal's whole no-progress window.</summary>
    NoProgress,
    /// <summary>The route to the target keeps failing.</summary>
    RouteFailed,
    /// <summary>Every same-type target and legal alternative has been tried.</summary>
    NoAlternative,
    /// <summary>The critical world resource confirmed absent with no substitute.</summary>
    MissingWorldResource,
    /// <summary>A special world seed with no strategy provider.</summary>
    UnsupportedWorld,
    /// <summary>A modded gate with no automation provider.</summary>
    UnsupportedMod,
    /// <summary>Backpack full and no storage could be built.</summary>
    BackpackFullNoStorage,
    /// <summary>Repeated boss defeats; needs a real upgrade before retrying.</summary>
    BossRepeatedlyFailed,
    /// <summary>Code faulted; handed to the fault guard.</summary>
    Faulted
}

/// <summary>
/// The automation companion's persistent task-execution state (design §4.1). Before this existed
/// the brain chose one <see cref="AutoPlan"/> per tick and threw away all context, so "stuck after
/// X" had no answer. This carries the current goal, how long it has run without progress, how many
/// recovery attempts have been made and why, so the watchdog can escalate deliberately instead of
/// retrying the same failing action every frame.
///
/// Tick-based timers are rebuilt each session (a reload simply restarts the current window, which
/// the design explicitly permits); only the cross-session counters that must survive a save/reload
/// — the boss failure tally — are persisted, through <see cref="AutoCompanionState"/>.
/// </summary>
public sealed class AutoTaskRuntime
{
    /// <summary>Tiles of net relocation that count as travel progress within a soft window.</summary>
    public const float ProgressDistanceTiles = 6f;

    public AutoGoalId CurrentGoal = AutoGoalId.Idle;
    public AutoGoalId CurrentStep = AutoGoalId.Idle;
    public long CurrentPlanKey;
    public long GoalStartedTick;
    public long StepStartedTick;

    /// <summary>How many recovery attempts the current goal has needed.</summary>
    public int AttemptCount;

    /// <summary>Current rung on the recovery ladder (0 = none). See <see cref="AutoWatchdog"/>.</summary>
    public int RecoveryLevel;

    public AutoTaskResult LastResult = AutoTaskResult.Running;
    public AutoBlockReason LastFailureReason = AutoBlockReason.None;

    /// <summary>The goal's task-metric signature last tick. Progress is defined as this value
    /// changing at all — a gather count rising, wood falling as it is placed, empty slots rising as
    /// surplus is deposited — never as raw coordinates moving.</summary>
    public long LastProgressSignature;

    public int TicksWithoutProgress;
    /// <summary>Ticks with no task-metric change at all, ignoring travel — the hard ceiling that
    /// catches a companion walking forever toward something it can never reach.</summary>
    public int TicksWithoutMetricChange;

    public Vector2 WindowStartPosition;
    public Vector2 LastKnownPosition;
    public Point LastTargetTile = new(-1, -1);

    /// <summary>The controller-owned target retained across ticks. Without this, every scan selects
    /// the same nearest bad tree/ore/pot again and the watchdog cannot actually change targets.</summary>
    public Point? ActiveTargetTile;
    public AutoGoalId ActiveTargetGoal = AutoGoalId.Idle;
    public float BestTargetDistanceSquared = float.MaxValue;

    /// <summary>Incremented when a controller observes a real world-side accomplishment such as its
    /// retained target tile disappearing. Inventory counts alone miss that progress.</summary>
    public long ProgressSerial;
    public long LastObservedProgressSerial;

    /// <summary>While the game tick is below this, the current (repeatedly RetryLater'd or blocked)
    /// goal is on backoff and is not re-executed — the fix for infinite same-frame retries.</summary>
    public long BlockedUntilTick;

    /// <summary>Cross-session: number of times a boss attempt has ended in defeat/wipe.</summary>
    public int BossFailureCount;

    private const int RejectedTargetCapacity = 24;
    private const int PlanDeferralCapacity = 16;
    private readonly List<RejectedTarget> rejectedTargets = [];
    private readonly Dictionary<long, long> deferredPlans = [];

    public int RejectedTargetCount => rejectedTargets.Count;
    public int DeferredPlanCount => deferredPlans.Count;

    // Ring log of the most recent state transitions (design §4.3). Transient; write-on-transition
    // only, so it never spams per frame.
    private const int LogCapacity = 12;
    private readonly string[] transitionLog = new string[LogCapacity];
    private int logHead;
    private int logCount;

    public void Reset()
    {
        CurrentGoal = AutoGoalId.Idle;
        CurrentStep = AutoGoalId.Idle;
        CurrentPlanKey = 0;
        AttemptCount = 0;
        RecoveryLevel = 0;
        TicksWithoutProgress = 0;
        TicksWithoutMetricChange = 0;
        LastResult = AutoTaskResult.Running;
        LastFailureReason = AutoBlockReason.None;
        BlockedUntilTick = 0;
        LastTargetTile = new Point(-1, -1);
        ClearActiveTarget();
        ProgressSerial = 0;
        LastObservedProgressSerial = 0;
        rejectedTargets.Clear();
        deferredPlans.Clear();
    }

    public bool TryGetActiveTarget(AutoGoalId goal, System.Func<Point, bool> valid, out Point target)
    {
        if (ActiveTargetGoal == goal && ActiveTargetTile is Point active && valid(active))
        {
            target = active;
            return true;
        }

        // A target which was valid on the previous tick and is now gone is world progress, not a
        // navigation failure. This distinction is important for trees and mined blocks.
        if (ActiveTargetGoal == goal && ActiveTargetTile.HasValue)
            RecordWorldProgress();
        ClearActiveTarget();
        target = default;
        return false;
    }

    public void SetActiveTarget(AutoGoalId goal, Point target)
    {
        if (ActiveTargetGoal == goal && ActiveTargetTile == target)
            return;
        ActiveTargetGoal = goal;
        ActiveTargetTile = target;
        LastTargetTile = target;
        BestTargetDistanceSquared = float.MaxValue;
        Log($"target {goal} {target.X},{target.Y}");
    }

    public void ClearActiveTarget()
    {
        ActiveTargetGoal = AutoGoalId.Idle;
        ActiveTargetTile = null;
        BestTargetDistanceSquared = float.MaxValue;
    }

    public void RejectActiveTarget(int radiusTiles, int cooldownTicks = 7200)
    {
        if (ActiveTargetTile is not Point target)
            return;

        PruneExpiredTargets((long)Main.GameUpdateCount);
        if (rejectedTargets.Count >= RejectedTargetCapacity)
            rejectedTargets.RemoveAt(0);
        rejectedTargets.Add(new RejectedTarget(ActiveTargetGoal, target,
            System.Math.Max(1, radiusTiles), (long)Main.GameUpdateCount + cooldownTicks));
        Log($"reject {ActiveTargetGoal} {target.X},{target.Y}");
        ClearActiveTarget();
    }

    public bool IsTargetRejected(AutoGoalId goal, Point target)
    {
        long now = (long)Main.GameUpdateCount;
        PruneExpiredTargets(now);
        foreach (RejectedTarget rejected in rejectedTargets)
        {
            if (rejected.Goal != goal)
                continue;
            int dx = rejected.Center.X - target.X;
            int dy = rejected.Center.Y - target.Y;
            if (dx * dx + dy * dy <= rejected.RadiusTiles * rejected.RadiusTiles)
                return true;
        }
        return false;
    }

    public void RecordWorldProgress() => ProgressSerial++;

    public void Defer(AutoPlan plan, int ticks)
    {
        if (deferredPlans.Count >= PlanDeferralCapacity && !deferredPlans.ContainsKey(plan.StableKey))
        {
            long oldestKey = 0;
            long oldestUntil = long.MaxValue;
            foreach ((long key, long until) in deferredPlans)
            {
                if (until < oldestUntil)
                {
                    oldestKey = key;
                    oldestUntil = until;
                }
            }
            deferredPlans.Remove(oldestKey);
        }
        deferredPlans[plan.StableKey] = (long)Main.GameUpdateCount + System.Math.Max(60, ticks);
        Log($"defer {plan.Goal} +{ticks}t");
    }

    public bool IsDeferred(AutoPlan plan)
    {
        if (!deferredPlans.TryGetValue(plan.StableKey, out long until))
            return false;
        if (until > (long)Main.GameUpdateCount)
            return true;
        deferredPlans.Remove(plan.StableKey);
        return false;
    }

    private void PruneExpiredTargets(long now)
    {
        for (int i = rejectedTargets.Count - 1; i >= 0; i--)
        {
            if (rejectedTargets[i].UntilTick <= now)
                rejectedTargets.RemoveAt(i);
        }
    }

    private readonly struct RejectedTarget
    {
        public readonly AutoGoalId Goal;
        public readonly Point Center;
        public readonly int RadiusTiles;
        public readonly long UntilTick;

        public RejectedTarget(AutoGoalId goal, Point center, int radiusTiles, long untilTick)
        {
            Goal = goal;
            Center = center;
            RadiusTiles = radiusTiles;
            UntilTick = untilTick;
        }
    }

    public void Log(string entry)
    {
        transitionLog[logHead] = $"[{Main.GameUpdateCount % 100000}] {entry}";
        logHead = (logHead + 1) % LogCapacity;
        if (logCount < LogCapacity)
            logCount++;
    }

    /// <summary>Most-recent-first snapshot of the transition ring log.</summary>
    public System.Collections.Generic.IEnumerable<string> RecentLog()
    {
        for (int i = 0; i < logCount; i++)
        {
            int index = (logHead - 1 - i + LogCapacity) % LogCapacity;
            if (transitionLog[index] is not null)
                yield return transitionLog[index];
        }
    }

    /// <summary>
    /// The goal's task-metric signature: a scalar that changes only when the task actually
    /// accomplishes something. Deliberately not a function of raw position (design §4.1) — travel is
    /// accounted for separately by net displacement in the watchdog.
    /// </summary>
    public static long ComputeSignature(AutoContext ctx, AutoPlan plan)
    {
        CompanionProfile profile = ctx.Profile;
        switch (plan.Goal)
        {
            case AutoGoalId.GatherWood:
                return AutoInventory.Count(profile, ItemID.Wood);
            case AutoGoalId.GatherStone:
                return AutoInventory.Count(profile, ItemID.StoneBlock);
            case AutoGoalId.GatherGel:
                return AutoInventory.Count(profile, ItemID.Gel) * 1000L + AutoInventory.Count(profile, ItemID.Torch);
            case AutoGoalId.MineOre:
                return AutoResourceController.CountBasicOre(profile);
            case AutoGoalId.MineIronTierOre:
                return AutoInventory.Count(profile, ItemID.IronOre) + AutoInventory.Count(profile, ItemID.LeadOre);
            case AutoGoalId.CraftStation:
                // Station present (the big term) plus how much of the station item is held.
                return (StationExists(ctx, plan.TileType) ? 1_000_000L : 0L) +
                    AutoInventory.Count(profile, plan.ItemType);
            case AutoGoalId.CraftGear:
                return CountBars(profile);
            case AutoGoalId.CraftConsumable:
                return AutoInventory.Count(profile, plan.ItemType);
            case AutoGoalId.BuildShelter:
                return (ctx.State.ShelterCompleted ? 1_000_000L : 0L) + AutoInventory.Count(profile, ItemID.Wood);
            case AutoGoalId.DepositSurplus:
                return EmptyBackpackSlots(profile);
            default:
                return 0L;
        }
    }

    private static bool StationExists(AutoContext ctx, int tileType) =>
        AutoWorldKnowledge.FindNearestTileOfType(ctx.Me.Center, 16, tileType) is not null;

    private static long CountBars(CompanionProfile profile)
    {
        long total = 0;
        foreach (int type in AutoProgressionPlanner.SmeltableBarItems)
            total += AutoInventory.Count(profile, type);
        return total;
    }

    private static long EmptyBackpackSlots(CompanionProfile profile)
    {
        long empty = 0;
        foreach (Item item in profile.Backpack)
        {
            if (item.IsAir)
                empty++;
        }
        return empty;
    }
}

namespace MoreAIPlayer.Companion.Automation;

/// <summary>How freely the scheduler may postpone a plan when its current route is expensive or
/// repeatedly failing. This describes the objective, not the implementation used to reach it.</summary>
public enum AutoPlanKind : byte
{
    /// <summary>A world/progression requirement. It cannot be abandoned, but its current route may
    /// be deferred while the companion performs useful preparation.</summary>
    HardGate,
    /// <summary>A personal capability requirement that may be satisfied by any equivalent item or
    /// effect, including an unexpectedly strong world drop.</summary>
    Capability,
    /// <summary>Safe preparation that can run while a harder route is cooling down.</summary>
    Support,
    /// <summary>A worthwhile but non-essential improvement. It may be skipped when risk outweighs
    /// its remaining value.</summary>
    Optional
}

/// <summary>
/// One tick's decision from the planner: which goal to pursue and the parameters the executing
/// controller needs. A value type so choosing a goal allocates nothing.
/// </summary>
public readonly struct AutoPlan
{
    public readonly AutoGoalId Goal;

    /// <summary>Item to craft/place (station or product), when relevant.</summary>
    public readonly int ItemType;

    /// <summary>Placed tile type for a station goal, when relevant.</summary>
    public readonly int TileType;

    /// <summary>Target amount for a gather goal, when relevant.</summary>
    public readonly int Amount;

    /// <summary>Why the companion is idle, when <see cref="Goal"/> is <see cref="AutoGoalId.Idle"/>.</summary>
    public readonly AutoTaskResult IdleReason;

    public readonly AutoPlanKind Kind;

    /// <summary>Recovery rungs allowed before the current route is cooled down and the scheduler
    /// looks for useful same-stage work. The objective itself remains eligible later.</summary>
    public readonly int FailureBudget;

    private AutoPlan(AutoGoalId goal, int itemType, int tileType, int amount,
        AutoTaskResult idleReason, AutoPlanKind kind, int failureBudget)
    {
        Goal = goal;
        ItemType = itemType;
        TileType = tileType;
        Amount = amount;
        IdleReason = idleReason;
        Kind = kind;
        FailureBudget = System.Math.Max(1, failureBudget);
    }

    /// <summary>Stable identity for one concrete plan. Work Bench and Furnace are both
    /// CraftStation goals, but they must have different watchdog histories.</summary>
    public long StableKey
    {
        get
        {
            unchecked
            {
                long key = (int)Goal + 17;
                key = key * 397 ^ ItemType;
                key = key * 397 ^ TileType;
                key = key * 397 ^ Amount;
                return key;
            }
        }
    }

    public static AutoPlan Gather(AutoGoalId goal, int amount,
        AutoPlanKind kind = AutoPlanKind.HardGate, int failureBudget = 3) =>
        new(goal, 0, 0, amount, AutoTaskResult.Running, kind, failureBudget);

    public static AutoPlan Station(int itemType, int tileType,
        AutoPlanKind kind = AutoPlanKind.HardGate, int failureBudget = 3) =>
        new(AutoGoalId.CraftStation, itemType, tileType, 0, AutoTaskResult.Running, kind, failureBudget);

    public static AutoPlan Product(int itemType,
        AutoPlanKind kind = AutoPlanKind.HardGate, int failureBudget = 3) =>
        new(AutoGoalId.CraftConsumable, itemType, 0, 0, AutoTaskResult.Running, kind, failureBudget);

    public static AutoPlan Simple(AutoGoalId goal,
        AutoPlanKind kind = AutoPlanKind.HardGate, int failureBudget = 3) =>
        new(goal, 0, 0, 0, AutoTaskResult.Running, kind, failureBudget);

    public static AutoPlan Idle(AutoTaskResult reason) =>
        new(AutoGoalId.Idle, 0, 0, 0, reason, AutoPlanKind.Support, 1);
}

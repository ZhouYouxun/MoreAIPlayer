namespace MoreAIPlayer.Companion.Automation;

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

    private AutoPlan(AutoGoalId goal, int itemType, int tileType, int amount, AutoTaskResult idleReason)
    {
        Goal = goal;
        ItemType = itemType;
        TileType = tileType;
        Amount = amount;
        IdleReason = idleReason;
    }

    public static AutoPlan Gather(AutoGoalId goal, int amount) => new(goal, 0, 0, amount, AutoTaskResult.Running);
    public static AutoPlan Station(int itemType, int tileType) => new(AutoGoalId.CraftStation, itemType, tileType, 0, AutoTaskResult.Running);
    public static AutoPlan Product(int itemType) => new(AutoGoalId.CraftConsumable, itemType, 0, 0, AutoTaskResult.Running);
    public static AutoPlan Simple(AutoGoalId goal) => new(goal, 0, 0, 0, AutoTaskResult.Running);
    public static AutoPlan Idle(AutoTaskResult reason) => new(AutoGoalId.Idle, 0, 0, 0, reason);
}

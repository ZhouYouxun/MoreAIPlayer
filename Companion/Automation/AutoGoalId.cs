namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// The high-level goal the planner has currently selected. This is the "what am I trying to do
/// right now" label; the controllers own "how". The planner picks one each tick from the current
/// capability gaps and world milestone (design §5, §11). Later phases extend the list toward the
/// boss goals; Phase 2 covers the early resource loop.
/// </summary>
public enum AutoGoalId
{
    /// <summary>Nothing productive to do (or globally blocked); stand safe and re-check.</summary>
    Idle,

    /// <summary>Chop trees for wood, replanting where possible.</summary>
    GatherWood,

    /// <summary>Mine surface/near-surface stone.</summary>
    GatherStone,

    /// <summary>Kill low-risk slimes for Gel (falls back to torches from pots/chests).</summary>
    GatherGel,

    /// <summary>Mine the best available pre-hardmode ore the current pick can break.</summary>
    MineOre,

    /// <summary>Mine iron or lead specifically for the first legal anvil.</summary>
    MineIronTierOre,

    /// <summary>Craft the next missing basic crafting station (Work Bench, Furnace, Anvil).</summary>
    CraftStation,

    /// <summary>Craft the next worthwhile basic gear the companion can make now.</summary>
    CraftGear,

    /// <summary>Craft a stackable prerequisite such as torches from collected materials.</summary>
    CraftConsumable,

    /// <summary>Build/repair the shelter, NPC rooms and storage chests.</summary>
    BuildShelter,

    /// <summary>Move surplus non-carry materials into the companion's own chests.</summary>
    DepositSurplus,

    /// <summary>Reach a town NPC and buy a whitelisted item (Bug Net, an anvil, …).</summary>
    Shop
}

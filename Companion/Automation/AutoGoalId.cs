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
    Shop,

    /// <summary>Travel to and legally quick-loot a nearby worthwhile unlocked chest.</summary>
    LootChest,

    /// <summary>Keep scouting the world for drops, chests and encounters instead of idling.</summary>
    ExploreWorld,

    /// <summary>Emergency use of the pickaxe on breakable solid tiles intersecting the body.</summary>
    EscapeTerrain,

    /// <summary>Spend a bounded number of casts at a nearby viable pond when rod and bait exist.</summary>
    FishOpportunity,

    /// <summary>Defeat a live world boss; active boss combat outranks every optional route.</summary>
    FightBoss,

    /// <summary>Mine an indexed Life Crystal or Life Fruit anywhere in the world.</summary>
    AcquireVitalUpgrade,

    /// <summary>After a death, travel from home to the remembered failure location.</summary>
    ReturnToDeathSite,

    /// <summary>Clear and platform the arena appropriate for the current campaign milestone.</summary>
    PrepareArena,

    /// <summary>Reach the Demolitionist and buy a bounded stock of bombs/dynamite.</summary>
    AcquireExplosives,

    /// <summary>Legally start the current progression encounter once preparation is complete.</summary>
    SummonBoss,

    /// <summary>Retreat, heal, change route, or enlarge preparation after unacceptable live risk.</summary>
    RiskRecovery,

    /// <summary>Mine stage ore, build hardmode stations and craft class-directed armour/tools.</summary>
    UpgradeClassLoadout,

    /// <summary>Run one bounded vanilla weapon-upgrade route selected by the phone decision tree.</summary>
    AcquireWeaponUpgrade,

    /// <summary>Craft or buy real ammunition for a high-value ranged weapon.</summary>
    AcquireCombatSupplies
}
